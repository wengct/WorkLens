using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class AzureDevOpsWorkItemSourceAdapterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backfill_reports_progress_for_history_and_filtered_content(bool matchesIdentity)
    {
        var updates = new List<SourceCollectionProgress>();
        var history = matchesIdentity
            ? """{"value":[{"revisedDate":"2026-09-08T02:00:00Z","revisedBy":{"uniqueName":"me@example.com"},"fields":{"System.State":{"oldValue":"New","newValue":"Active"},"System.ChangedDate":{"newValue":"2026-09-08T02:00:00Z"}}}]}"""
            : "[]";
        var adapter = new AzureDevOpsPullRequestSourceAdapter(new AzureDevOpsCliService(CreateSingleWorkItemRunner(history)));
        var source = CreateWorkItemOnlySource();
        await adapter.CollectAsync(new CollectionRequest(source, DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            UpdateCheckpoint: false, Progress: updates.Add), CancellationToken.None);
        Assert.Contains(updates, item => item.Stage.Contains("查詢工作項目清單"));
        Assert.Contains(updates, item => item.Stage.Contains("異動紀錄 0/1"));
        Assert.Contains(updates, item => item.Stage.Contains("異動紀錄 1/1"));
        Assert.Contains(updates, item => item.Stage.Contains(matchesIdentity ? "內容與留言 0/1" : "內容與留言 0/0"));
        if (matchesIdentity) Assert.Contains(updates, item => item.Stage.Contains("內容與留言 1/1"));
        Assert.All(updates, item => Assert.Equal(source.Id, item.SourceId));
    }

    [Fact]
    public async Task ListChangedWorkItemsAsync_uses_date_only_WIQL_with_an_exact_boundary_filter_afterward()
    {
        string? wiql = null;
        var runner = new FakeProcessRunner((request, _) =>
        {
            var wiqlIndex = request.Arguments.ToList().IndexOf("--wiql");
            if (wiqlIndex >= 0)
            {
                wiql = request.Arguments[wiqlIndex + 1];
            }

            return Json("""{"workItems":[]}""");
        });
        var service = new AzureDevOpsCliService(runner);

        await service.ListChangedWorkItemsAsync(
            "https://dev.azure.com/example",
            new AzureDevOpsWorkItemScope { ProjectId = "project", ProjectName = "Project" },
            DateTimeOffset.Parse("2026-09-08T15:08:27Z"),
            DateTimeOffset.Parse("2026-09-09T00:00:00Z"));

        Assert.Equal(
            "SELECT [System.Id] FROM WorkItems WHERE [System.ChangedDate] >= '2026-09-08' AND [System.ChangedDate] < '2026-09-10' ORDER BY [System.ChangedDate] ASC",
            wiql);
        Assert.DoesNotMatch(@"'\d{4}-\d{2}-\d{2}T", wiql);
    }

    [Fact]
    public async Task ListChangedWorkItemsAsync_accepts_JSON_preceded_by_a_CLI_diagnostic_line()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(
            0,
            "WARNING: The Azure DevOps extension is installed in preview.\n{\"workItems\":[{\"id\":42,\"url\":\"https://dev.azure.com/example/project/_apis/wit/workItems/42\"}]}",
            string.Empty));
        var service = new AzureDevOpsCliService(runner);

        var workItems = await service.ListChangedWorkItemsAsync(
            "https://dev.azure.com/example",
            new AzureDevOpsWorkItemScope { ProjectId = "project", ProjectName = "Project" },
            DateTimeOffset.Parse("2026-09-08T00:00:00Z"),
            DateTimeOffset.Parse("2026-09-09T00:00:00Z"));

        Assert.Equal(42, Assert.Single(workItems).Id);
    }

    [Fact]
    public async Task ListChangedWorkItemsAsync_includes_the_unparseable_CLI_output_in_the_error()
    {
        var runner = new FakeProcessRunner((_, _) => new ProcessResult(0, string.Empty, "CLI diagnostic"));
        var service = new AzureDevOpsCliService(runner);

        var exception = await Assert.ThrowsAsync<AzureDevOpsCliException>(() => service.ListChangedWorkItemsAsync(
            "https://dev.azure.com/example",
            new AzureDevOpsWorkItemScope { ProjectId = "project", ProjectName = "Project" },
            DateTimeOffset.Parse("2026-09-08T00:00:00Z"),
            DateTimeOffset.Parse("2026-09-09T00:00:00Z")));

        Assert.Contains("原始 Azure CLI 輸出", exception.Message, StringComparison.Ordinal);
        Assert.Contains("命令階段：boards query", exception.Message, StringComparison.Ordinal);
        Assert.Contains("結束碼：0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("stdout：0 個字元", exception.Message, StringComparison.Ordinal);
        Assert.Contains("stderr：14 個字元", exception.Message, StringComparison.Ordinal);
        Assert.Contains("CLI diagnostic", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_successful_query_allows_validation_and_collection_without_warnings()
    {
        var runner = new FakeProcessRunner((request, _) => request.Arguments[0] switch
        {
            "version" => Json("""{"azure-cli":"2.0"}"""),
            "extension" => Json("""{"version":"1.0"}"""),
            "account" => Json("""{"user":{"name":"me@example.com","type":"user"}}"""),
            "ad" => Json("""{"id":"entra-id","displayName":"Me","userPrincipalName":"me@example.com"}"""),
            "boards" when request.Arguments[1] == "query" => new ProcessResult(0, "", ""),
            _ => throw new Xunit.Sdk.XunitException("Unexpected command")
        });
        var adapter = new AzureDevOpsPullRequestSourceAdapter(new AzureDevOpsCliService(runner));
        var source = CreateWorkItemOnlySource();

        var validation = await adapter.ValidateAsync(source, CancellationToken.None);
        Assert.True(validation.IsValid, validation.Summary);
        Assert.Equal(SourceHealthStatus.Ready, validation.Status);

        var batch = await adapter.CollectAsync(
            new CollectionRequest(source, DateTimeOffset.Parse("2026-09-08T00:00:00Z")),
            CancellationToken.None);
        Assert.Empty(batch.Evidence);
        Assert.Empty(batch.Warnings);
        Assert.Equal(1, batch.SuccessfulRepositories);
    }

    [Theory]
    [InlineData(1, "", "", false)]
    [InlineData(0, "", "", true)]
    [InlineData(0, "{broken", "", false)]
    public async Task Query_failures_are_not_treated_as_empty_results(
        int exitCode, string stdout, string stderr, bool timedOut)
    {
        var service = new AzureDevOpsCliService(new FakeProcessRunner(
            (_, _) => new ProcessResult(exitCode, stdout, stderr, timedOut)));
        await Assert.ThrowsAsync<AzureDevOpsCliException>(() => service.ListChangedWorkItemsAsync(
            "https://dev.azure.com/example",
            new AzureDevOpsWorkItemScope { ProjectId = "project", ProjectName = "Project" },
            DateTimeOffset.Parse("2026-09-08T00:00:00Z"), null));
    }

    [Fact]
    public async Task GetWorkItemCommentsAsync_uses_a_CLI_compatible_preview_API_version()
    {
        string? apiVersion = null;
        var runner = new FakeProcessRunner((request, _) =>
        {
            var arguments = request.Arguments.ToList();
            var apiVersionIndex = arguments.IndexOf("--api-version");
            apiVersion = arguments[apiVersionIndex + 1];
            return Json("""{"comments":[]}""");
        });

        await new AzureDevOpsCliService(runner).GetWorkItemCommentsAsync(
            "https://dev.azure.com/example",
            new AzureDevOpsWorkItemScope { ProjectId = "project", ProjectName = "Project" },
            86909);

        Assert.Equal("7.1-preview", apiVersion);
    }

    [Fact]
    public async Task CollectAsync_keeps_only_current_users_changes_and_discussions_and_merges_them_by_day()
    {
        var runner = new FakeProcessRunner((request, _) =>
        {
            var command = string.Join(' ', request.Arguments);
            if (command.StartsWith("account show ", StringComparison.Ordinal))
            {
                return Json("""{"user":{"name":"me@example.com","type":"user"}}""");
            }
            if (command.StartsWith("ad signed-in-user show ", StringComparison.Ordinal))
            {
                return Json("""{"id":"entra-id","displayName":"Me","userPrincipalName":"me@example.com"}""");
            }
            if (command.StartsWith("boards query ", StringComparison.Ordinal))
            {
                return Json("""{"workItems":[{"id":42,"url":"https://dev.azure.com/example/project/_apis/wit/workItems/42"}]}""");
            }
            if (command.StartsWith("boards work-item show ", StringComparison.Ordinal))
            {
                return Json("""
                    {"id":42,"url":"https://dev.azure.com/example/project/_apis/wit/workItems/42","fields":{"System.WorkItemType":"Task","System.Title":"Review feature","System.State":"Active","System.AssignedTo":{"uniqueName":"other@example.com"}},"relations":[]}
                    """);
            }
            if (command.Contains("--resource updates", StringComparison.Ordinal))
            {
                return Json("""
                    {"value":[
                      {"revisedDate":"2026-09-08T01:00:00Z","revisedBy":{"uniqueName":"other@example.com"},"fields":{"System.State":{"oldValue":"New","newValue":"Active"}}},
                      {"revisedDate":"2026-09-08T02:00:00Z","revisedBy":{"uniqueName":"me@example.com"},"fields":{"System.State":{"oldValue":"New","newValue":"Active"},"System.ChangedDate":{"oldValue":"2026-09-08T01:59:00Z","newValue":"2026-09-08T02:00:00Z"}}},
                      {"revisedDate":"9999-01-01T00:00:00Z","revisedBy":{"uniqueName":"me@example.com"},"fields":{"System.PersonId":{"oldValue":"old-person","newValue":"new-person"}}}
                    ]}
                    """);
            }
            if (command.Contains("--resource comments", StringComparison.Ordinal))
            {
                return Json("""
                    {"comments":[
                      {"id":1,"createdDate":"2026-09-08T03:00:00Z","createdBy":{"uniqueName":"me@example.com"},"text":"我完成 review"},
                      {"id":2,"createdDate":"2026-09-08T04:00:00Z","createdBy":{"uniqueName":"other@example.com"},"text":"其他人的留言"}
                    ]}
                    """);
            }
            throw new Xunit.Sdk.XunitException($"Unexpected Azure CLI command: {command}");
        });
        var source = new ActivitySource
        {
            Id = Guid.NewGuid(),
            SourceType = ActivitySourceType.AzureDevOpsPullRequest,
            SettingsJson = SourceSettingsSerializer.Serialize(new AzureDevOpsSourceSettings
            {
                OrganizationUrl = "https://dev.azure.com/example",
                CollectPullRequests = false,
                CollectWorkItems = true,
                WorkItemScopes = [new AzureDevOpsWorkItemScope
                {
                    ProjectId = "project",
                    ProjectName = "Project",
                    WorkLensProjectId = Guid.NewGuid()
                }]
            })
        };

        var batch = await new AzureDevOpsPullRequestSourceAdapter(new AzureDevOpsCliService(runner)).CollectAsync(
            new CollectionRequest(source, DateTimeOffset.Parse("2026-09-08T00:00:00Z")),
            CancellationToken.None);

        var evidence = Assert.Single(batch.Evidence);
        Assert.Equal(EvidenceKind.AzureDevOpsWorkItemActivity, evidence.Kind);
        Assert.Contains("Review feature", evidence.Title, StringComparison.Ordinal);
        var metadata = SourceSettingsSerializer.DeserializeAzureDevOpsWorkItemActivityMetadata(evidence.MetadataJson);
        Assert.NotNull(metadata);
        Assert.Single(metadata!.FieldChanges);
        Assert.Equal("System.State", metadata.FieldChanges[0].Field);
        Assert.Single(metadata.Discussions);
        Assert.Equal("我完成 review", metadata.Discussions[0].Text);
        Assert.Equal(1, batch.SuccessfulRepositories);
    }

    [Fact]
    public async Task CollectAsync_does_not_treat_a_superseded_revision_as_current_activity()
    {
        var runner = CreateSingleWorkItemRunner("""
            {"value":[
              {"revisedDate":"2026-09-10T06:39:11.577Z","revisedBy":{"uniqueName":"me@example.com"},"fields":{"System.State":{"oldValue":"Committed","newValue":"Done"},"System.ChangedDate":{"oldValue":"2026-07-01T06:26:04.307Z","newValue":"2026-07-01T06:27:47.970Z"}}}
            ]}
            """);

        var batch = await CollectSingleWorkItemAsync(
            runner,
            DateTimeOffset.Parse("2026-09-10T00:00:00Z"),
            DateTimeOffset.Parse("2026-09-11T00:00:00Z"));

        Assert.Empty(batch.Evidence);
    }

    [Fact]
    public async Task CollectAsync_collects_the_current_revision_using_its_changed_date()
    {
        var runner = CreateSingleWorkItemRunner("""
            {"value":[
              {"revisedDate":"9999-01-01T00:00:00Z","revisedBy":{"uniqueName":"me@example.com"},"fields":{"System.State":{"oldValue":"Committed","newValue":"Done"},"System.ChangedDate":{"oldValue":"2026-09-09T06:39:11.577Z","newValue":"2026-09-10T06:39:11.577Z"}}}
            ]}
            """);

        var batch = await CollectSingleWorkItemAsync(
            runner,
            DateTimeOffset.Parse("2026-09-10T00:00:00Z"),
            DateTimeOffset.Parse("2026-09-11T00:00:00Z"));

        var evidence = Assert.Single(batch.Evidence);
        Assert.Equal(DateTimeOffset.Parse("2026-09-10T06:39:11.577Z"), evidence.OccurredAt);
        var metadata = SourceSettingsSerializer.DeserializeAzureDevOpsWorkItemActivityMetadata(evidence.MetadataJson);
        Assert.NotNull(metadata);
        Assert.Equal(DateTimeOffset.Parse("2026-09-10T06:39:11.577Z"), Assert.Single(metadata!.FieldChanges).ChangedAt);
    }

    [Fact]
    public async Task CollectAsync_reads_details_and_discussions_only_for_updates_that_may_belong_to_the_current_user()
    {
        var commands = new List<string>();
        var runner = new FakeProcessRunner((request, _) =>
        {
            var command = string.Join(' ', request.Arguments);
            lock (commands)
            {
                commands.Add(command);
            }

            if (command.StartsWith("account show ", StringComparison.Ordinal))
            {
                return Json("""{"user":{"name":"me@example.com","type":"user"}}""");
            }
            if (command.StartsWith("ad signed-in-user show ", StringComparison.Ordinal))
            {
                return Json("""{"id":"entra-id","displayName":"Me","userPrincipalName":"me@example.com"}""");
            }
            if (command.StartsWith("boards query ", StringComparison.Ordinal))
            {
                return Json("""
                    {"workItems":[
                      {"id":41,"url":"https://dev.azure.com/example/project/_apis/wit/workItems/41"},
                      {"id":87211,"url":"https://dev.azure.com/example/project/_apis/wit/workItems/87211"}
                    ]}
                    """);
            }
            if (command.Contains("--resource updates", StringComparison.Ordinal))
            {
                if (command.Contains("id=41", StringComparison.Ordinal))
                {
                    return Json("""{"value":[{"revisedDate":"2026-09-08T02:00:00Z","revisedBy":{"uniqueName":"other@example.com"},"fields":{"System.State":{"oldValue":"New","newValue":"Active"}}}]}""");
                }

                return Json("""{"value":[{"revisedDate":"9999-01-01T00:00:00Z","revisedBy":{"uniqueName":"me@example.com"},"fields":{"System.History":{"oldValue":"","newValue":"留言"}}}]}""");
            }
            if (command.StartsWith("boards work-item show ", StringComparison.Ordinal))
            {
                if (command.Contains("--id 41", StringComparison.Ordinal))
                {
                    throw new Xunit.Sdk.XunitException("不屬於目前使用者的異動不應讀取完整 Work Item。");
                }

                return Json("""
                    {"id":87211,"url":"https://dev.azure.com/example/project/_apis/wit/workItems/87211","fields":{"System.WorkItemType":"Task","System.Title":"留言活動","System.State":"Active"},"relations":[]}
                    """);
            }
            if (command.Contains("--resource comments", StringComparison.Ordinal))
            {
                return Json("""{"comments":[{"id":1,"createdDate":"2026-09-08T15:39:35.427Z","createdBy":{"uniqueName":"me@example.com"},"text":"我的留言"}]}""");
            }

            throw new Xunit.Sdk.XunitException($"Unexpected Azure CLI command: {command}");
        });
        var source = CreateWorkItemOnlySource();

        var batch = await new AzureDevOpsPullRequestSourceAdapter(new AzureDevOpsCliService(runner)).CollectAsync(
            new CollectionRequest(source, DateTimeOffset.Parse("2026-09-08T15:30:00Z")),
            CancellationToken.None);

        var evidence = Assert.Single(batch.Evidence);
        Assert.Contains("#87211", evidence.Title, StringComparison.Ordinal);
        Assert.DoesNotContain(commands, command => command.StartsWith("boards work-item show ", StringComparison.Ordinal) && command.Contains("--id 41", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, command => command.Contains("--resource comments", StringComparison.Ordinal) && command.Contains("workItemId=41", StringComparison.Ordinal));
    }

    [Fact]
    public void DeserializeAzureDevOps_keeps_legacy_sources_PR_only()
    {
        var settings = SourceSettingsSerializer.DeserializeAzureDevOps("""
            {"organizationUrl":"https://dev.azure.com/example","scopes":[{"projectId":"project","repositoryId":"repo","targetBranch":"main"}]}
            """);

        Assert.True(settings.CollectPullRequests);
        Assert.False(settings.CollectWorkItems);
        Assert.Single(settings.Scopes);
        Assert.Empty(settings.WorkItemScopes);
    }

    private static ActivitySource CreateWorkItemOnlySource() => new()
    {
        Id = Guid.NewGuid(),
        SourceType = ActivitySourceType.AzureDevOpsPullRequest,
        SettingsJson = SourceSettingsSerializer.Serialize(new AzureDevOpsSourceSettings
        {
            OrganizationUrl = "https://dev.azure.com/example",
            CollectPullRequests = false,
            CollectWorkItems = true,
            WorkItemScopes = [new AzureDevOpsWorkItemScope
            {
                ProjectId = "project",
                ProjectName = "Project",
                WorkLensProjectId = Guid.NewGuid()
            }]
        })
    };

    private static ProcessResult Json(string output) => new(0, output, string.Empty);

    private static FakeProcessRunner CreateSingleWorkItemRunner(string updatesJson) => new((request, _) =>
    {
        var command = string.Join(' ', request.Arguments);
        if (command.StartsWith("account show ", StringComparison.Ordinal))
        {
            return Json("""{"user":{"name":"me@example.com","type":"user"}}""");
        }
        if (command.StartsWith("ad signed-in-user show ", StringComparison.Ordinal))
        {
            return Json("""{"id":"entra-id","displayName":"Me","userPrincipalName":"me@example.com"}""");
        }
        if (command.StartsWith("boards query ", StringComparison.Ordinal))
        {
            return Json("""{"workItems":[{"id":85054,"url":"https://dev.azure.com/example/project/_apis/wit/workItems/85054"}]}""");
        }
        if (command.Contains("--resource updates", StringComparison.Ordinal))
        {
            return Json(updatesJson);
        }
        if (command.StartsWith("boards work-item show ", StringComparison.Ordinal))
        {
            return Json("""
                {"id":85054,"url":"https://dev.azure.com/example/project/_apis/wit/workItems/85054","fields":{"System.WorkItemType":"Product Backlog Item","System.Title":"API Client Management","System.State":"Done"},"relations":[]}
                """);
        }
        if (command.Contains("--resource comments", StringComparison.Ordinal))
        {
            return Json("""{"comments":[]}""");
        }

        throw new Xunit.Sdk.XunitException($"Unexpected Azure CLI command: {command}");
    });

    private static Task<CollectionBatch> CollectSingleWorkItemAsync(
        FakeProcessRunner runner,
        DateTimeOffset since,
        DateTimeOffset until)
    {
        var source = new ActivitySource
        {
            Id = Guid.NewGuid(),
            SourceType = ActivitySourceType.AzureDevOpsPullRequest,
            SettingsJson = SourceSettingsSerializer.Serialize(new AzureDevOpsSourceSettings
            {
                OrganizationUrl = "https://dev.azure.com/example",
                CollectPullRequests = false,
                CollectWorkItems = true,
                WorkItemScopes = [new AzureDevOpsWorkItemScope
                {
                    ProjectId = "project",
                    ProjectName = "Project",
                    WorkLensProjectId = Guid.NewGuid()
                }]
            })
        };

        return new AzureDevOpsPullRequestSourceAdapter(new AzureDevOpsCliService(runner)).CollectAsync(
            new CollectionRequest(source, since, until),
            CancellationToken.None);
    }

    private sealed class FakeProcessRunner(Func<ProcessRequest, string?, ProcessResult> handler) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            string? standardInput = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(handler(request, standardInput));
    }
}
