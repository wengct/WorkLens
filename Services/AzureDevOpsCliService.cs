using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed record AzureDevOpsCliDiagnostics(
    bool CliInstalled,
    string? CliVersion,
    bool ExtensionInstalled,
    string? ExtensionVersion,
    bool IsLoggedIn,
    string? AccountName,
    bool IsInteractiveUser,
    bool IsValid,
    string Summary,
    IReadOnlyList<string> Details);

public sealed record AzureDevOpsOption(string Id, string Name);

// Id is reserved for an Azure DevOps IdentityRef identifier. EntraObjectId comes from
// az ad signed-in-user show and must not be compared with a PR's createdBy.id.
public sealed record AzureDevOpsIdentity(
    string Id,
    string DisplayName,
    string UniqueName,
    string? ResolutionWarning = null,
    string? EntraObjectId = null);

public sealed record AzureDevOpsPullRequestRecord(
    int Id,
    string Title,
    string Description,
    string Status,
    bool IsDraft,
    string Url,
    string CreatorId,
    string CreatorName,
    string CreatorUniqueName,
    string SourceBranch,
    string TargetBranch,
    DateTimeOffset CreationDate,
    DateTimeOffset? ClosedDate,
    string ProjectName,
    string RepositoryName);

public sealed record AzureDevOpsWorkItemReference(int Id, string Url);

public sealed record AzureDevOpsWorkItemFieldUpdate(string Field, string OldValue, string NewValue);

public sealed record AzureDevOpsWorkItemUpdate(
    DateTimeOffset ChangedDate,
    string RevisedBy,
    IReadOnlyList<AzureDevOpsWorkItemFieldUpdate> Fields);

public sealed record AzureDevOpsWorkItemComment(
    int Id,
    DateTimeOffset CreatedDate,
    string CreatedBy,
    DateTimeOffset? ModifiedDate,
    string ModifiedBy,
    bool IsDeleted,
    string Text);

public sealed class AzureDevOpsCliException(string message) : InvalidOperationException(message);

public sealed class AzureDevOpsCliService(IProcessRunner processRunner)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);

    public async Task<AzureDevOpsCliDiagnostics> DiagnoseAsync(
        string? organizationUrl = null,
        CancellationToken cancellationToken = default)
    {
        var details = new List<string>();
        var versionResult = await RunAzAsync(["version"], VersionTimeout, cancellationToken);
        if (!versionResult.Succeeded)
        {
            return new AzureDevOpsCliDiagnostics(
                false, null, false, null, false, null, false, false,
                "找不到 Azure CLI，請先安裝。", ["WorkLens 只會讀取 Azure CLI 狀態，不會自動安裝。"]);
        }

        var cliVersion = ParseVersion(versionResult.StandardOutput, "azure-cli");
        details.Add(cliVersion is null ? "Azure CLI 已安裝。" : $"Azure CLI {cliVersion}。");

        var extensionResult = await RunAzAsync(["extension", "show", "--name", "azure-devops"], VersionTimeout, cancellationToken);
        var extensionInstalled = extensionResult.Succeeded;
        var extensionVersion = extensionInstalled ? ParseProperty(extensionResult.StandardOutput, "version") : null;
        details.Add(extensionInstalled
            ? extensionVersion is null ? "Azure DevOps CLI extension 已安裝。" : $"Azure DevOps CLI extension {extensionVersion}。"
            : "尚未安裝 Azure DevOps CLI extension，請執行 az extension add --name azure-devops。");

        var accountResult = await RunAzAsync(["account", "show"], VersionTimeout, cancellationToken);
        if (!accountResult.Succeeded)
        {
            details.Add("尚未登入 Azure CLI，請執行 az login。");
            return new AzureDevOpsCliDiagnostics(
                true, cliVersion, extensionInstalled, extensionVersion, false, null, false, false,
                "Azure CLI 尚未登入。", details);
        }

        var account = ParseObject(accountResult.StandardOutput);
        var accountUser = GetObject(account, "user");
        var accountName = GetString(accountUser, "name");
        var accountType = GetString(accountUser, "type");
        var isInteractiveUser = string.Equals(accountType, "user", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(accountName);
        if (!isInteractiveUser)
        {
            details.Add("目前登入身分不是一般使用者；為保護資料，不會收集 PR。 ");
            return new AzureDevOpsCliDiagnostics(
                true, cliVersion, extensionInstalled, extensionVersion, true, accountName, false, false,
                "Azure CLI 已登入，但目前不是一般使用者身分。", details);
        }

        if (!extensionInstalled)
        {
            return new AzureDevOpsCliDiagnostics(
                true, cliVersion, false, null, true, accountName, true, false,
                "Azure CLI 已登入一般使用者，但尚未安裝 Azure DevOps CLI extension；請執行 az extension add --name azure-devops。", details);
        }

        if (string.IsNullOrWhiteSpace(organizationUrl))
        {
            details.Add($"已登入：{accountName}。請填入 Organization URL 以載入 Projects。 ");
            return new AzureDevOpsCliDiagnostics(
                true, cliVersion, true, extensionVersion, true, accountName, true, true,
                "Azure CLI 與 Azure DevOps extension 可用，已登入一般使用者。", details);
        }

        var organization = SourceSettingsSerializer.NormalizeAzureDevOpsOrganizationUrl(organizationUrl);
        if (!IsSupportedOrganizationUrl(organization))
        {
            details.Add("Organization URL 必須是 Azure DevOps Services HTTPS URL。 ");
            return new AzureDevOpsCliDiagnostics(
                true, cliVersion, true, extensionVersion, true, accountName, true, false,
                "Organization URL 格式無效。", details);
        }

        details.Add($"Organization URL 格式有效；將使用此 URL 載入 Projects（登入身分：{accountName}）。 ");

        return new AzureDevOpsCliDiagnostics(
            true, cliVersion, true, extensionVersion, true, accountName, true, true,
            "Azure DevOps CLI 設定有效；請由 Organization URL 載入 Projects。",
            details);
    }

    public async Task<AzureDevOpsIdentity> GetCurrentIdentityAsync(
        string organizationUrl,
        CancellationToken cancellationToken = default)
    {
        var accountResult = await RunAzAsync(["account", "show"], VersionTimeout, cancellationToken);
        if (!accountResult.Succeeded)
        {
            throw new AzureDevOpsCliException("Azure CLI 尚未登入，請執行 az login。");
        }

        var account = ParseObject(accountResult.StandardOutput);
        var accountUser = GetObject(account, "user");
        var accountName = GetString(accountUser, "name");
        var accountType = GetString(accountUser, "type");
        if (string.IsNullOrWhiteSpace(accountName) || !string.Equals(accountType, "user", StringComparison.OrdinalIgnoreCase))
        {
            throw new AzureDevOpsCliException("目前 Azure CLI 登入身分不是一般使用者，為保護資料不會收集 PR。");
        }

        _ = ValidateOrganizationUrl(organizationUrl);
        var candidates = new[]
            {
                accountName,
                GetString(accountUser, "email", "mail", "userPrincipalName", "principalName", "uniqueName"),
                GetString(account, "userName", "email", "mail")
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var graphResult = await RunAzAsync(
            ["ad", "signed-in-user", "show"],
            VersionTimeout,
            cancellationToken);
        if (graphResult.Succeeded)
        {
            try
            {
                var graphUser = ParseObject(graphResult.StandardOutput);
                var graphId = GetString(graphUser, "id");
                var graphDisplayName = GetString(graphUser, "displayName", "name");
                var graphUniqueName = GetString(
                    graphUser,
                    "userPrincipalName",
                    "mail",
                    "preferredUsername");
                var uniqueName = graphUniqueName ?? candidates.FirstOrDefault() ?? accountName;
                if (!string.IsNullOrWhiteSpace(graphId) || !string.IsNullOrWhiteSpace(graphUniqueName))
                {
                    return new AzureDevOpsIdentity(
                        string.Empty,
                        graphDisplayName ?? accountName,
                        uniqueName,
                        string.IsNullOrWhiteSpace(graphId)
                            ? "Azure CLI 沒有回傳 signed-in user ID；將以 UPN／帳號比對 PR creator。"
                            : null,
                        EntraObjectId: graphId);
                }
            }
            catch (AzureDevOpsCliException)
            {
                // Fall through to the account-name fallback below. The source must remain usable
                // when the signed-in user's Graph profile is unavailable or incomplete.
            }
        }

        var warning = "無法取得目前使用者的 Entra ID；將以 az login 帳號比對 PR creator。";
        var detail = graphResult.Succeeded ? "Azure CLI 沒有回傳 signed-in user ID。" : SanitizeError(graphResult.StandardError);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            warning = $"{warning} {detail}";
        }

        return new AzureDevOpsIdentity(
            string.Empty,
            accountName,
            candidates.FirstOrDefault() ?? accountName,
            warning);
    }

    public async Task<IReadOnlyList<AzureDevOpsOption>> GetProjectsAsync(
        string organizationUrl,
        CancellationToken cancellationToken = default)
    {
        var organization = ValidateOrganizationUrl(organizationUrl);
        var result = await RunAzAsync(
            ["devops", "project", "list", "--org", organization],
            CommandTimeout,
            cancellationToken);
        EnsureSucceeded(result, "讀取 Azure DevOps Project 失敗。");
        return ParseOptions(result.StandardOutput);
    }

    public async Task<IReadOnlyList<AzureDevOpsOption>> GetRepositoriesAsync(
        string organizationUrl,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var organization = ValidateOrganizationUrl(organizationUrl);
        var result = await RunAzAsync(
            ["repos", "list", "--org", organization, "--project", projectId],
            CommandTimeout,
            cancellationToken);
        EnsureSucceeded(result, "讀取 Azure DevOps Repo 失敗。");
        return ParseOptions(result.StandardOutput);
    }

    public async Task<IReadOnlyList<AzureDevOpsOption>> GetBranchesAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        CancellationToken cancellationToken = default)
    {
        var organization = ValidateOrganizationUrl(organizationUrl);
        var result = await RunAzAsync(
            ["repos", "ref", "list", "--org", organization, "--project", projectId, "--repository", repositoryId, "--filter", "heads/"],
            CommandTimeout,
            cancellationToken);
        EnsureSucceeded(result, "讀取 Azure DevOps target branch 失敗。");
        var branches = ParseOptions(result.StandardOutput)
            .Select(option => new AzureDevOpsOption(
                SourceSettingsSerializer.NormalizeAzureDevOpsBranch(option.Id),
                SourceSettingsSerializer.NormalizeAzureDevOpsBranch(option.Name)))
            .Where(option => option.Id.Length > 0)
            .GroupBy(option => option.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return branches;
    }

    public async Task<IReadOnlyList<AzureDevOpsPullRequestRecord>> ListPullRequestsAsync(
        string organizationUrl,
        AzureDevOpsPullRequestScope scope,
        AzureDevOpsIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var organization = ValidateOrganizationUrl(organizationUrl);
        var targetBranch = SourceSettingsSerializer.NormalizeAzureDevOpsBranch(scope.TargetBranch);
        var all = new List<AzureDevOpsPullRequestRecord>();
        const int pageSize = 100;
        for (var skip = 0; ; skip += pageSize)
        {
            var result = await RunAzAsync(
                [
                    "repos", "pr", "list",
                    "--org", organization,
                    "--project", scope.ProjectId,
                    "--repository", scope.RepositoryId,
                    "--include-links",
                    "--creator", string.IsNullOrWhiteSpace(identity.UniqueName) ? identity.DisplayName : identity.UniqueName,
                    "--target-branch", targetBranch,
                    "--status", "all",
                    "--skip", skip.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--top", pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ],
                CommandTimeout,
                cancellationToken);
            EnsureSucceeded(result, $"讀取 Repo「{scope.RepositoryName}」的 PR 失敗。");
            var page = ParsePullRequests(result.StandardOutput, organization, scope);
            all.AddRange(page);
            if (page.Count < pageSize)
            {
                break;
            }
        }

        return all
            .Where(pr => IsCreatedByIdentity(pr, identity))
            .GroupBy(pr => pr.Id)
            .Select(group => group.First())
            .ToList();
    }

    private static bool IsCreatedByIdentity(
        AzureDevOpsPullRequestRecord pullRequest,
        AzureDevOpsIdentity identity)
    {
        if (!string.IsNullOrWhiteSpace(identity.Id))
        {
            return string.Equals(pullRequest.CreatorId, identity.Id, StringComparison.OrdinalIgnoreCase);
        }

        // Azure DevOps PR creator.id is an Azure DevOps IdentityRef identifier. It is not the
        // Entra object id returned by az ad signed-in-user show, so the latter is deliberately
        // never compared with CreatorId. Use the account/UPN fields as the namespace-safe
        // fallback when no Azure DevOps IdentityRef id is available.
        return !string.IsNullOrWhiteSpace(identity.UniqueName) &&
               (string.Equals(pullRequest.CreatorUniqueName, identity.UniqueName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pullRequest.CreatorId, identity.UniqueName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<AzureDevOpsWorkItemReference>> ListPullRequestWorkItemsAsync(
        string organizationUrl,
        int pullRequestId,
        CancellationToken cancellationToken = default)
    {
        var organization = ValidateOrganizationUrl(organizationUrl);
        var result = await RunAzAsync(
            ["repos", "pr", "work-item", "list", "--id", pullRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--org", organization],
            CommandTimeout,
            cancellationToken);
        EnsureSucceeded(result, $"讀取 PR #{pullRequestId} 的關聯 work item 失敗。");
        return ParseWorkItemReferences(result.StandardOutput);
    }

    public async Task<AzureDevOpsWorkItemMetadata> GetWorkItemAsync(
        string organizationUrl,
        AzureDevOpsWorkItemReference reference,
        CancellationToken cancellationToken = default)
    {
        var organization = ValidateOrganizationUrl(organizationUrl);
        var result = await RunAzAsync(
            ["boards", "work-item", "show", "--id", reference.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "--expand", "all", "--org", organization],
            CommandTimeout,
            cancellationToken);
        EnsureSucceeded(result, $"讀取 work item #{reference.Id} 失敗。");
        return ParseWorkItem(result.StandardOutput, organization, reference);
    }

    public async Task<IReadOnlyList<AzureDevOpsWorkItemReference>> ListChangedWorkItemsAsync(
        string organizationUrl,
        AzureDevOpsWorkItemScope scope,
        DateTimeOffset since,
        DateTimeOffset? until,
        CancellationToken cancellationToken = default)
    {
        var organization = ValidateOrganizationUrl(organizationUrl);
        // Azure Boards organizations configured with date precision reject time values in WIQL.
        // This intentionally makes the candidate query date-broad; the adapter applies the exact
        // timestamp boundary to revisions and discussions before it creates evidence.
        var start = DateOnly.FromDateTime(since.UtcDateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var clauses = new List<string> { $"[System.ChangedDate] >= '{start}'" };
        if (until is DateTimeOffset end)
        {
            var endText = DateOnly.FromDateTime(end.UtcDateTime).AddDays(1)
                .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            clauses.Add($"[System.ChangedDate] < '{endText}'");
        }
        var wiql = $"SELECT [System.Id] FROM WorkItems WHERE {string.Join(" AND ", clauses)} ORDER BY [System.ChangedDate] ASC";
        var result = await RunAzAsync(
            ["boards", "query", "--org", organization, "--project", scope.ProjectId, "--wiql", wiql],
            CommandTimeout,
            cancellationToken);
        EnsureSucceeded(result, $"讀取 Project「{scope.ProjectName}」的 Work Item 失敗。");
        return ParseWorkItemReferences(result.StandardOutput);
    }

    public async Task<IReadOnlyList<AzureDevOpsWorkItemUpdate>> GetWorkItemUpdatesAsync(
        string organizationUrl,
        AzureDevOpsWorkItemScope scope,
        int workItemId,
        CancellationToken cancellationToken = default)
    {
        var organization = ValidateOrganizationUrl(organizationUrl);
        var result = await RunAzAsync(
            [
                "devops", "invoke", "--org", organization, "--area", "wit", "--resource", "updates",
                "--route-parameters", $"project={scope.ProjectId}", $"id={workItemId}", "--api-version", "7.1"
            ],
            CommandTimeout,
            cancellationToken);
        EnsureSucceeded(result, $"讀取 Work Item #{workItemId} 的異動歷程失敗。");
        return ParseWorkItemUpdates(result.StandardOutput);
    }

    public async Task<IReadOnlyList<AzureDevOpsWorkItemComment>> GetWorkItemCommentsAsync(
        string organizationUrl,
        AzureDevOpsWorkItemScope scope,
        int workItemId,
        CancellationToken cancellationToken = default)
    {
        var organization = ValidateOrganizationUrl(organizationUrl);
        var all = new List<AzureDevOpsWorkItemComment>();
        string? continuationToken = null;
        do
        {
            var arguments = new List<string>
            {
                "devops", "invoke", "--org", organization, "--area", "wit", "--resource", "comments",
                "--route-parameters", $"project={scope.ProjectId}", $"workItemId={workItemId}",
                "--query-parameters", "$top=200"
            };
            if (continuationToken is not null)
            {
                arguments.Add($"continuationToken={continuationToken}");
            }
            arguments.Add("--api-version");
            // az devops invoke parses a preview revision such as 7.1-preview.4 as a float
            // and fails with "could not convert string to float". The preview channel keeps
            // the endpoint contract while remaining compatible with the Azure CLI parser.
            arguments.Add("7.1-preview");
            var result = await RunAzAsync(
                arguments,
                CommandTimeout,
                cancellationToken);
            EnsureSucceeded(result, $"讀取 Work Item #{workItemId} 的 Discussion 失敗。");
            var page = ParseWorkItemComments(result.StandardOutput, out continuationToken);
            all.AddRange(page);
        } while (!string.IsNullOrWhiteSpace(continuationToken));

        return all.GroupBy(comment => comment.Id).Select(group => group.Last()).ToList();
    }

    public static bool IsIdentityMatch(string candidate, AzureDevOpsIdentity identity) =>
        !string.IsNullOrWhiteSpace(candidate) &&
        (string.Equals(candidate, identity.UniqueName, StringComparison.OrdinalIgnoreCase) ||
         (!string.IsNullOrWhiteSpace(identity.Id) && string.Equals(candidate, identity.Id, StringComparison.OrdinalIgnoreCase)));

    public static string ValidateOrganizationUrl(string value)
    {
        var organization = SourceSettingsSerializer.NormalizeAzureDevOpsOrganizationUrl(value);
        if (!IsSupportedOrganizationUrl(organization))
        {
            throw new AzureDevOpsCliException("Organization URL 必須是 Azure DevOps Services HTTPS URL。");
        }

        return organization;
    }

    public static bool IsSupportedOrganizationUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            uri.UserInfo.Length > 0 ||
            uri.Query.Length > 0 ||
            uri.Fragment.Length > 0)
        {
            return false;
        }

        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath
                .Trim('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                is [{ Length: > 0 }];
        }

        return uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) &&
               uri.Host.Length > ".visualstudio.com".Length &&
               uri.AbsolutePath.Trim('/').Length == 0;
    }

    public static string ToPlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Regex.Replace(WebUtility.HtmlDecode(value), "<[^>]+>", " ")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();
    }

    private async Task<ProcessResult> RunAzAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var command = arguments.Concat(["--only-show-errors", "--output", "json"]).ToArray();
        return await processRunner.RunAsync(
            new ProcessRequest("az", command),
            timeout: timeout,
            cancellationToken: cancellationToken);
    }

    private static void EnsureSucceeded(ProcessResult result, string message)
    {
        if (result.Succeeded)
        {
            return;
        }

        var detail = SanitizeError(result.StandardError);
        throw new AzureDevOpsCliException(detail.Length == 0 ? message : $"{message} {detail}");
    }

    private static string SanitizeError(string error)
    {
        var firstLine = error.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        if (firstLine.StartsWith('{') ||
            firstLine.StartsWith('[') ||
            Regex.IsMatch(firstLine, @"\b(token|password|secret|pat)\b", RegexOptions.IgnoreCase))
        {
            return string.Empty;
        }

        firstLine = Regex.Replace(firstLine, @"\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b", "[redacted-account]", RegexOptions.IgnoreCase);
        firstLine = Regex.Replace(firstLine, @"https?://\S+", "[redacted-url]", RegexOptions.IgnoreCase);
        return firstLine.Length > 240 ? firstLine[..240] : firstLine;
    }

    private static string? ParseVersion(string json, string packageName)
    {
        var root = ParseObject(json);
        var package = GetObject(root, packageName);
        return package.ValueKind == JsonValueKind.Object
            ? GetString(package, "version")
            : GetString(root, packageName);
    }

    private static IReadOnlyList<AzureDevOpsOption> ParseOptions(string json)
    {
        using var document = ParseDocument(json);
        var values = GetArrayResult(document.RootElement);
        var result = new List<AzureDevOpsOption>();
        foreach (var item in values)
        {
            var id = GetString(item, "id", "name") ?? string.Empty;
            var name = GetString(item, "name", "displayName", "id") ?? id;
            if (id.Length > 0 && name.Length > 0)
            {
                result.Add(new AzureDevOpsOption(id, name));
            }
        }

        return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<JsonElement> GetArrayResult(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray();
        }

        var value = GetProperty(root, "value", "items", "results", "workItems", "comments");
        return value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : Enumerable.Empty<JsonElement>();
    }

    private static List<AzureDevOpsPullRequestRecord> ParsePullRequests(
        string json,
        string organizationUrl,
        AzureDevOpsPullRequestScope scope)
    {
        using var document = ParseDocument(json);
        var result = new List<AzureDevOpsPullRequestRecord>();
        foreach (var item in GetArrayResult(document.RootElement))
        {
            var id = GetInt(item, "pullRequestId", "id");
            var creator = GetObject(item, "createdBy", "creator");
            var creationDate = GetDateTimeOffset(item, "creationDate", "createdAt");
            if (id <= 0 || creationDate == default)
            {
                continue;
            }

            result.Add(new AzureDevOpsPullRequestRecord(
                id,
                GetString(item, "title") ?? $"PR #{id}",
                GetString(item, "description") ?? string.Empty,
                GetString(item, "status") ?? string.Empty,
                GetBoolean(item, "isDraft"),
                GetPullRequestWebUrl(item, id, organizationUrl, scope),
                GetString(creator, "id") ?? string.Empty,
                GetString(creator, "displayName", "name") ?? string.Empty,
                GetString(creator, "uniqueName", "mail", "email", "mailAddress", "principalName") ?? string.Empty,
                SourceSettingsSerializer.NormalizeAzureDevOpsBranch(GetString(item, "sourceRefName", "sourceBranch")),
                SourceSettingsSerializer.NormalizeAzureDevOpsBranch(GetString(item, "targetRefName", "targetBranch")),
                creationDate,
                GetNullableDateTimeOffset(item, "closedDate", "closedAt"),
                scope.ProjectName,
                scope.RepositoryName));
        }

        return result;
    }

    private static string GetPullRequestWebUrl(
        JsonElement pullRequest,
        int pullRequestId,
        string organizationUrl,
        AzureDevOpsPullRequestScope scope)
    {
        var linkedWebUrl = GetNestedString(
            pullRequest,
            ["_links", "web", "href"],
            ["_links", "html", "href"]);
        var explicitWebUrl = GetString(pullRequest, "webUrl");
        var apiUrl = GetString(pullRequest, "url");
        return ToPullRequestWebUrl(
                   linkedWebUrl ?? explicitWebUrl ?? apiUrl,
                   pullRequestId,
                   organizationUrl,
                   scope.ProjectName,
                   scope.RepositoryName)
            ?? linkedWebUrl
            ?? explicitWebUrl
            ?? apiUrl
            ?? string.Empty;
    }

    public static string? ToPullRequestWebUrl(
        string? sourceUrl,
        int pullRequestId,
        string? organizationUrl = null,
        string? projectIdOrName = null,
        string? repositoryName = null)
    {
        if (pullRequestId <= 0)
        {
            return null;
        }

        if (TryCreateHttpsUri(sourceUrl, out var uri) && IsPullRequestWebUrl(uri, pullRequestId))
        {
            return uri.AbsoluteUri;
        }

        const string repositoryApiMarker = "/_apis/git/repositories/";
        if (TryCreateHttpsUri(sourceUrl, out uri))
        {
            var markerIndex = uri.AbsolutePath.IndexOf(repositoryApiMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                var repositoryPath = uri.AbsolutePath[(markerIndex + repositoryApiMarker.Length)..]
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(repositoryPath))
                {
                    var projectPath = uri.AbsolutePath[..markerIndex].TrimEnd('/');
                    return $"{uri.GetLeftPart(UriPartial.Authority)}{projectPath}/_git/{repositoryPath}/pullrequest/{pullRequestId}";
                }
            }
        }

        if (TryCreateHttpsUri(organizationUrl, out var organization) &&
            IsSupportedOrganizationUrl(organization.AbsoluteUri) &&
            !string.IsNullOrWhiteSpace(projectIdOrName) &&
            !string.IsNullOrWhiteSpace(repositoryName))
        {
            return $"{organization.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(projectIdOrName.Trim())}/_git/{Uri.EscapeDataString(repositoryName.Trim())}/pullrequest/{pullRequestId}";
        }

        return null;
    }

    public static string? ToWorkItemWebUrl(
        string? sourceUrl,
        int workItemId,
        string? organizationUrl = null,
        string? projectIdOrName = null)
    {
        if (workItemId <= 0)
        {
            return null;
        }

        if (TryCreateHttpsUri(organizationUrl, out var organization) &&
            IsSupportedOrganizationUrl(organization.AbsoluteUri) &&
            !string.IsNullOrWhiteSpace(projectIdOrName))
        {
            return $"{organization.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(projectIdOrName.Trim())}/_workitems/edit/{workItemId}";
        }

        if (TryCreateHttpsUri(sourceUrl, out var source) && IsWorkItemWebUrl(source, workItemId))
        {
            return source.AbsoluteUri;
        }

        const string workItemApiMarker = "/_apis/wit/workitems/";
        if (TryCreateHttpsUri(sourceUrl, out source))
        {
            var markerIndex = source.AbsolutePath.IndexOf(workItemApiMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                var projectPath = source.AbsolutePath[..markerIndex].TrimEnd('/');
                return $"{source.GetLeftPart(UriPartial.Authority)}{projectPath}/_workitems/edit/{workItemId}";
            }
        }

        return null;
    }

    private static bool IsPullRequestWebUrl(Uri uri, int pullRequestId)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        const string marker = "/pullrequest/";
        var markerIndex = path.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var idText = path[(markerIndex + marker.Length)..];
        return int.TryParse(idText, out var parsedId) && parsedId == pullRequestId &&
               path[..markerIndex].Contains("/_git/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorkItemWebUrl(Uri uri, int workItemId)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        const string marker = "/_workitems/edit/";
        var markerIndex = path.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        return int.TryParse(path[(markerIndex + marker.Length)..], out var parsedId) && parsedId == workItemId;
    }

    private static bool TryCreateHttpsUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri!) &&
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        uri = null!;
        return false;
    }

    private static IReadOnlyList<AzureDevOpsWorkItemReference> ParseWorkItemReferences(string json)
    {
        using var document = ParseDocument(json);
        return GetArrayResult(document.RootElement)
            .Select(item => new AzureDevOpsWorkItemReference(
                GetInt(item, "id"),
                GetNestedString(item, ["_links", "html", "href"], ["_links", "web", "href"])
                    ?? GetString(item, "url", "webUrl")
                    ?? string.Empty))
            .Where(item => item.Id > 0)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
    }

    private static IReadOnlyList<AzureDevOpsWorkItemUpdate> ParseWorkItemUpdates(string json)
    {
        using var document = ParseDocument(json);
        return GetArrayResult(document.RootElement)
            .Select(item =>
            {
                var revisedBy = GetObject(item, "revisedBy");
                var fields = new List<AzureDevOpsWorkItemFieldUpdate>();
                var fieldsElement = GetProperty(item, "fields");
                if (fieldsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in fieldsElement.EnumerateObject())
                    {
                        fields.Add(new AzureDevOpsWorkItemFieldUpdate(
                            field.Name,
                            JsonValueToText(GetProperty(field.Value, "oldValue")),
                            JsonValueToText(GetProperty(field.Value, "newValue"))));
                    }
                }
                return new AzureDevOpsWorkItemUpdate(
                    GetDateTimeOffset(GetObject(fieldsElement, "System.ChangedDate"), "newValue"),
                    GetIdentityName(revisedBy),
                    fields);
            })
            .OrderBy(item => item.ChangedDate)
            .ToList();
    }

    private static IReadOnlyList<AzureDevOpsWorkItemComment> ParseWorkItemComments(string json, out string? continuationToken)
    {
        using var document = ParseDocument(json);
        continuationToken = GetString(document.RootElement, "continuationToken");
        return GetArrayResult(document.RootElement)
            .Select(item => new AzureDevOpsWorkItemComment(
                GetInt(item, "id"),
                GetDateTimeOffset(item, "createdDate", "createdOnBehalfDate"),
                GetIdentityName(GetObject(item, "createdBy", "createdOnBehalfOf")),
                GetNullableDateTimeOffset(item, "modifiedDate"),
                GetIdentityName(GetObject(item, "modifiedBy")),
                GetBoolean(item, "isDeleted"),
                GetString(item, "text", "renderedText") ?? string.Empty))
            .Where(item => item.Id > 0 && item.CreatedDate != default)
            .ToList();
    }

    private static AzureDevOpsWorkItemMetadata ParseWorkItem(
        string json,
        string organizationUrl,
        AzureDevOpsWorkItemReference reference)
    {
        using var document = ParseDocument(json);
        var root = document.RootElement;
        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var fieldsElement = GetProperty(root, "fields");
        if (fieldsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in fieldsElement.EnumerateObject())
            {
                fields[property.Name] = property.Value.Clone();
            }
        }

        var relations = new List<AzureDevOpsWorkItemRelation>();
        var relationsElement = GetProperty(root, "relations");
        if (relationsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var relation in relationsElement.EnumerateArray())
            {
                relations.Add(new AzureDevOpsWorkItemRelation
                {
                    Rel = GetString(relation, "rel") ?? string.Empty,
                    Url = GetString(relation, "url") ?? string.Empty,
                    AttributesJson = GetProperty(relation, "attributes").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                        ? null
                        : GetProperty(relation, "attributes").GetRawText()
                });
            }
        }

        var nestedWebUrl = GetNestedString(
            root,
            ["_links", "html", "href"],
            ["_links", "web", "href"]);
        var sourceUrl = nestedWebUrl ?? GetString(root, "url") ?? reference.Url;
        var workItemId = GetInt(root, "id");
        if (workItemId <= 0)
        {
            workItemId = reference.Id;
        }

        var workItemUrl = ToWorkItemWebUrl(sourceUrl, workItemId, organizationUrl);

        return new AzureDevOpsWorkItemMetadata
        {
            Id = workItemId,
            Url = workItemUrl ?? sourceUrl,
            Type = GetFieldString(fields, "System.WorkItemType"),
            Title = GetFieldString(fields, "System.Title"),
            State = GetFieldString(fields, "System.State"),
            AssignedTo = GetFieldString(fields, "System.AssignedTo"),
            Description = GetFieldString(fields, "System.Description"),
            AcceptanceCriteria = GetFieldString(fields, "Microsoft.VSTS.Common.AcceptanceCriteria"),
            Tags = GetFieldString(fields, "System.Tags"),
            Fields = fields,
            Relations = relations
        };
    }

    private static string GetFieldString(IReadOnlyDictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Object => GetString(value, "displayName", "uniqueName", "mail", "email", "mailAddress", "principalName") ?? value.GetRawText(),
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => value.GetRawText()
        };
    }

    private static string GetIdentityName(JsonElement identity) =>
        GetString(identity, "uniqueName", "mail", "email", "mailAddress", "principalName", "id") ?? string.Empty;

    private static string JsonValueToText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Object => GetIdentityName(value) is { Length: > 0 } identity ? identity : value.GetRawText(),
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.GetRawText()
    };

    private static JsonElement ParseObject(string json)
    {
        using var document = ParseDocument(json);
        return document.RootElement.Clone();
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new AzureDevOpsCliException("Azure CLI 回傳無法解析的 JSON。");
        }
    }

    private static JsonElement GetObject(JsonElement element, params string[] names)
    {
        var value = GetProperty(element, names);
        return value.ValueKind == JsonValueKind.Object ? value : default;
    }

    private static string? ParseProperty(string json, string name)
    {
        var root = ParseObject(json);
        return GetString(root, name);
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        var value = GetProperty(element, names);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                ? value.ToString()
                : null;
    }

    private static string? GetNestedString(JsonElement element, params string[][] paths)
    {
        foreach (var path in paths)
        {
            var current = element;
            foreach (var name in path)
            {
                current = GetProperty(current, name);
                if (current.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                {
                    break;
                }
            }

            if (current.ValueKind == JsonValueKind.String)
            {
                return current.GetString();
            }
        }

        return null;
    }

    private static int GetInt(JsonElement element, params string[] names)
    {
        var value = GetProperty(element, names);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : int.TryParse(GetString(element, names), out number) ? number : 0;
    }

    private static bool GetBoolean(JsonElement element, params string[] names)
    {
        var value = GetProperty(element, names);
        return value.ValueKind == JsonValueKind.True ||
               (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var result) && result);
    }

    private static DateTimeOffset GetDateTimeOffset(JsonElement element, params string[] names) =>
        GetNullableDateTimeOffset(element, names) ?? default;

    private static DateTimeOffset? GetNullableDateTimeOffset(JsonElement element, params string[] names)
    {
        var value = GetString(element, names);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static JsonElement GetProperty(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        foreach (var name in names)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }
        }

        return default;
    }
}
