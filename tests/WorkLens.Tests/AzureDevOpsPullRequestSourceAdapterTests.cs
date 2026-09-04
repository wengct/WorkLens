using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class AzureDevOpsPullRequestSourceAdapterTests
{
    [Fact]
    public async Task Collection_filters_creator_adds_created_and_closed_events_and_caches_work_items()
    {
        var workItemId = 42;
        var runner = new FakeProcessRunner((request, _) =>
        {
            var command = string.Join(' ', request.Arguments);
            if (command.StartsWith("version ", StringComparison.Ordinal))
            {
                return Json("""{"azure-cli":"2.70.0"}""");
            }

            if (command.StartsWith("extension show ", StringComparison.Ordinal))
            {
                return Json("""{"name":"azure-devops","version":"1.0.0"}""");
            }

            if (command.StartsWith("account show ", StringComparison.Ordinal))
            {
                return Json("""{"user":{"name":"login@example.com","type":"user"}}""");
            }

            if (command.StartsWith("ad signed-in-user show ", StringComparison.Ordinal))
            {
                return Json("""{"id":"entra-user-1","displayName":"Me","userPrincipalName":"me@example.com"}""");
            }

            if (command.StartsWith("repos pr list ", StringComparison.Ordinal))
            {
                return Json("""
                    [
                      {
                        "pullRequestId": 10,
                        "title": "Active PR",
                        "description": "Active description",
                        "status": "active",
                        "isDraft": false,
                        "url": "https://dev.azure.com/example/project-1/_apis/git/repositories/repo-1/pullRequests/10",
                        "_links": {"web": {"href": "https://dev.azure.com/example/project-1/_git/repo-1/pullrequest/10"}},
                        "createdBy": {"id":"user-1","displayName":"Me","uniqueName":"me@example.com"},
                        "sourceRefName": "refs/heads/feature/active",
                        "targetRefName": "refs/heads/main",
                        "creationDate": "2026-09-02T09:00:00+00:00"
                      },
                      {
                        "pullRequestId": 11,
                        "title": "Completed PR",
                        "description": "Completed description",
                        "status": "completed",
                        "isDraft": false,
                        "url": "https://dev.azure.com/example/project-1/_apis/git/repositories/repo-1/pullRequests/11",
                        "createdBy": {"id":"user-1","displayName":"Me","uniqueName":"me@example.com"},
                        "sourceRefName": "refs/heads/feature/completed",
                        "targetRefName": "refs/heads/main",
                        "creationDate": "2026-08-20T09:00:00+00:00",
                        "closedDate": "2026-09-03T10:00:00+00:00"
                      },
                      {
                        "pullRequestId": 12,
                        "title": "Other user PR",
                        "description": "Should be dropped",
                        "status": "completed",
                        "createdBy": {"id":"entra-user-1","displayName":"Other","uniqueName":"other@example.com"},
                        "targetRefName": "refs/heads/main",
                        "creationDate": "2026-09-03T11:00:00+00:00",
                        "closedDate": "2026-09-03T12:00:00+00:00"
                      },
                      {
                        "pullRequestId": 13,
                        "title": "Abandoned PR",
                        "description": "Abandoned description",
                        "status": "abandoned",
                        "isDraft": false,
                        "url": "https://dev.azure.com/example/project-1/_apis/git/repositories/repo-1/pullRequests/13",
                        "createdBy": {"id":"user-1","displayName":"Me","uniqueName":"me@example.com"},
                        "sourceRefName": "refs/heads/feature/abandoned",
                        "targetRefName": "refs/heads/main",
                        "creationDate": "2026-08-20T09:00:00+00:00",
                        "closedDate": "2026-09-04T10:00:00+00:00"
                      }
                    ]
                    """);
            }

            if (command.StartsWith("repos pr work-item list ", StringComparison.Ordinal))
            {
                return Json($"[{{\"id\":{workItemId},\"url\":\"https://dev.azure.com/example/_workitems/edit/{workItemId}\"}}]");
            }

            if (command.StartsWith("boards work-item show ", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "id": 42,
                      "url": "https://dev.azure.com/example/_workitems/edit/42",
                      "fields": {
                        "System.WorkItemType": "Product Backlog Item",
                        "System.Title": "Implement feature",
                        "System.State": "Active",
                        "System.AssignedTo": {"displayName":"Me","uniqueName":"me@example.com"},
                        "System.Description": "<p>Details</p>",
                        "Microsoft.VSTS.Common.AcceptanceCriteria": "<p>It works</p>",
                        "System.Tags": "feature; ado"
                      },
                      "relations": [
                        {"rel":"System.LinkTypes.Hierarchy-Forward","url":"https://dev.azure.com/example/_workitems/edit/7","attributes":{"name":"Child"}}
                      ]
                    }
                    """);
            }

            throw new Xunit.Sdk.XunitException($"Unexpected Azure CLI command: {command}");
        });
        var adapter = new AzureDevOpsPullRequestSourceAdapter(new AzureDevOpsCliService(runner));
        var scope = new AzureDevOpsPullRequestScope
        {
            ProjectId = "project-1",
            ProjectName = "Project",
            RepositoryId = "repo-1",
            RepositoryName = "Repo",
            TargetBranch = "refs/heads/main",
            WorkLensProjectId = Guid.NewGuid()
        };
        var source = new ActivitySource
        {
            Id = Guid.NewGuid(),
            SourceType = ActivitySourceType.AzureDevOpsPullRequest,
            SettingsJson = SourceSettingsSerializer.Serialize(new AzureDevOpsSourceSettings
            {
                OrganizationUrl = "https://dev.azure.com/example",
                Scopes = [scope]
            })
        };

        var batch = await adapter.CollectAsync(
            new CollectionRequest(
                source,
                DateTimeOffset.Parse("2026-09-01T00:00:00+00:00"),
                DateTimeOffset.Parse("2026-09-05T00:00:00+00:00")),
            CancellationToken.None);

        Assert.Equal(1, batch.SuccessfulRepositories);
        Assert.Empty(batch.Warnings);
        Assert.Equal(3, batch.Evidence.Count);
        Assert.Contains(batch.Evidence, item => item.Kind == EvidenceKind.AzureDevOpsPullRequestCreated && item.Title.Contains("Active PR"));
        Assert.Contains(batch.Evidence, item => item.Kind == EvidenceKind.AzureDevOpsPullRequestClosed && item.Title.Contains("Completed PR"));
        Assert.Contains(batch.Evidence, item => item.Kind == EvidenceKind.AzureDevOpsPullRequestClosed && item.Title.Contains("Abandoned PR"));
        Assert.All(batch.Evidence, item => Assert.Equal(scope.WorkLensProjectId, item.ProjectId));
        var metadata = SourceSettingsSerializer.DeserializeAzureDevOpsMetadata(batch.Evidence[0].MetadataJson);
        Assert.NotNull(metadata);
        Assert.Single(metadata!.WorkItems);
        Assert.Equal("Me", metadata.WorkItems[0].AssignedTo);
        Assert.Equal("https://dev.azure.com/example/project-1/_git/repo-1/pullrequest/10", metadata.Url);
        Assert.Equal("project-1", metadata.ProjectId);
        Assert.Single(runner.Requests, request => string.Join(' ', request.Arguments).StartsWith("boards work-item show ", StringComparison.Ordinal));
        var prList = runner.Requests.Single(request => string.Join(' ', request.Arguments).StartsWith("repos pr list ", StringComparison.Ordinal));
        Assert.Contains("--org https://dev.azure.com/example", string.Join(' ', prList.Arguments), StringComparison.Ordinal);
        Assert.Contains("--project project-1", string.Join(' ', prList.Arguments), StringComparison.Ordinal);
        Assert.Contains("--repository repo-1", string.Join(' ', prList.Arguments), StringComparison.Ordinal);
        Assert.Contains("--target-branch main", string.Join(' ', prList.Arguments), StringComparison.Ordinal);
        Assert.Contains("--creator me@example.com", string.Join(' ', prList.Arguments), StringComparison.Ordinal);
        Assert.Contains(scope.ProjectId, batch.CheckpointJson, StringComparison.Ordinal);
        var completedMetadata = SourceSettingsSerializer.DeserializeAzureDevOpsMetadata(
            batch.Evidence.Single(item => item.Title.Contains("Completed PR", StringComparison.Ordinal)).MetadataJson);
        Assert.Equal("https://dev.azure.com/example/project-1/_git/repo-1/pullrequest/11", completedMetadata!.Url);
        Assert.Contains(runner.Requests, request =>
            string.Join(' ', request.Arguments).StartsWith("ad signed-in-user show ", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Requests, request =>
            string.Join(' ', request.Arguments).StartsWith("devops user show ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diagnostics_rejects_service_principal_without_running_organization_commands()
    {
        var runner = new FakeProcessRunner((request, _) =>
        {
            var command = string.Join(' ', request.Arguments);
            if (command.StartsWith("version ", StringComparison.Ordinal))
            {
                return Json("""{"azure-cli":"2.70.0"}""");
            }

            if (command.StartsWith("extension show ", StringComparison.Ordinal))
            {
                return Json("""{"name":"azure-devops","version":"1.0.0"}""");
            }

            if (command.StartsWith("account show ", StringComparison.Ordinal))
            {
                return Json("""{"user":{"name":"service-principal","type":"servicePrincipal"}}""");
            }

            throw new Xunit.Sdk.XunitException($"Organization command should not run: {command}");
        });

        var diagnostics = await new AzureDevOpsCliService(runner).DiagnoseAsync(
            "https://dev.azure.com/example",
            CancellationToken.None);

        Assert.False(diagnostics.IsValid);
        Assert.False(diagnostics.IsInteractiveUser);
        Assert.Contains("一般使用者", diagnostics.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Requests, request =>
            string.Join(' ', request.Arguments).StartsWith("ad signed-in-user show ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diagnostics_reports_missing_extension_without_running_devops_commands()
    {
        var runner = new FakeProcessRunner((request, _) =>
        {
            var command = string.Join(' ', request.Arguments);
            if (command.StartsWith("version ", StringComparison.Ordinal))
            {
                return Json("""{"azure-cli":"2.70.0"}""");
            }

            if (command.StartsWith("extension show ", StringComparison.Ordinal))
            {
                return new ProcessResult(1, string.Empty, "extension not installed");
            }

            if (command.StartsWith("account show ", StringComparison.Ordinal))
            {
                return Json("""{"user":{"name":"me@example.com","type":"user"}}""");
            }

            throw new Xunit.Sdk.XunitException($"Command should not run: {command}");
        });

        var diagnostics = await new AzureDevOpsCliService(runner).DiagnoseAsync(
            "https://dev.azure.com/example",
            CancellationToken.None);

        Assert.True(diagnostics.CliInstalled);
        Assert.False(diagnostics.ExtensionInstalled);
        Assert.True(diagnostics.IsLoggedIn);
        Assert.False(diagnostics.IsValid);
        Assert.Contains("az extension add --name azure-devops", diagnostics.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Current_identity_reads_entra_id_without_treating_it_as_ado_creator_id()
    {
        var runner = new FakeProcessRunner((request, _) =>
        {
            var command = string.Join(' ', request.Arguments);
            if (command.StartsWith("account show ", StringComparison.Ordinal))
            {
                return Json("""{"user":{"name":"login@example.com","type":"user"}}""");
            }

            if (command.StartsWith("ad signed-in-user show ", StringComparison.Ordinal))
            {
                return Json("""{"id":"entra-user-1","displayName":"Me","userPrincipalName":"me@example.com"}""");
            }

            throw new Xunit.Sdk.XunitException($"ADO user lookup should not run: {command}");
        });

        var identity = await new AzureDevOpsCliService(runner).GetCurrentIdentityAsync(
            "https://dev.azure.com/example",
            CancellationToken.None);

        Assert.Empty(identity.Id);
        Assert.Equal("entra-user-1", identity.EntraObjectId);
        Assert.Equal("me@example.com", identity.UniqueName);
        Assert.DoesNotContain(runner.Requests, request =>
            string.Join(' ', request.Arguments).StartsWith("devops user show ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diagnostics_uses_org_url_only_for_format_validation_and_projects_are_loaded_separately()
    {
        var runner = new FakeProcessRunner((request, _) =>
        {
            var command = string.Join(' ', request.Arguments);
            if (command.StartsWith("version ", StringComparison.Ordinal))
            {
                return Json("""{"azure-cli":"2.70.0"}""");
            }

            if (command.StartsWith("extension show ", StringComparison.Ordinal))
            {
                return Json("""{"name":"azure-devops","version":"1.0.0"}""");
            }

            if (command.StartsWith("account show ", StringComparison.Ordinal))
            {
                return Json("""{"user":{"name":"me@example.com","type":"user"}}""");
            }

            if (command.StartsWith("devops project list ", StringComparison.Ordinal))
            {
                return Json("""{"continuationToken":null,"value":[{"id":"project-1","name":"Project"}]}""");
            }

            throw new Xunit.Sdk.XunitException($"Organization identity command should not run during diagnostics: {command}");
        });

        var service = new AzureDevOpsCliService(runner);
        var diagnostics = await service.DiagnoseAsync(
            "https://miniasp.visualstudio.com/",
            CancellationToken.None);

        Assert.True(diagnostics.IsValid);
        Assert.DoesNotContain(runner.Requests, request =>
            string.Join(' ', request.Arguments).StartsWith("ad signed-in-user show ", StringComparison.Ordinal));

        var projects = await service.GetProjectsAsync(
            "https://miniasp.visualstudio.com/",
            CancellationToken.None);

        Assert.Single(projects);
        var projectList = runner.Requests.Single(request =>
            string.Join(' ', request.Arguments).StartsWith("devops project list ", StringComparison.Ordinal));
        Assert.Contains("--org https://miniasp.visualstudio.com", string.Join(' ', projectList.Arguments), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collection_falls_back_to_login_name_when_signed_in_user_lookup_is_unavailable_and_keeps_creator_filter()
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
                return new ProcessResult(1, string.Empty, "signed-in user lookup is not available");
            }

            if (command.StartsWith("repos pr list ", StringComparison.Ordinal))
            {
                return Json("""
                    [
                      {
                        "pullRequestId": 21,
                        "title": "Other PR",
                        "status": "active",
                        "createdBy": {"id":"user-2","uniqueName":"other@example.com"},
                        "targetRefName": "refs/heads/main",
                        "creationDate": "2026-09-03T09:00:00+00:00"
                      },
                      {
                        "pullRequestId": 22,
                        "title": "My PR",
                        "status": "active",
                        "createdBy": {"id":"user-1","uniqueName":"me@example.com"},
                        "targetRefName": "refs/heads/main",
                        "creationDate": "2026-09-03T10:00:00+00:00"
                      }
                    ]
                    """);
            }

            if (command.StartsWith("repos pr work-item list ", StringComparison.Ordinal))
            {
                return Json("[]");
            }

            throw new Xunit.Sdk.XunitException($"Unexpected Azure CLI command: {command}");
        });
        var scope = new AzureDevOpsPullRequestScope
        {
            ProjectId = "project-1",
            ProjectName = "Project",
            RepositoryId = "repo-1",
            RepositoryName = "Repo",
            TargetBranch = "main",
            WorkLensProjectId = Guid.NewGuid()
        };
        var source = new ActivitySource
        {
            Id = Guid.NewGuid(),
            SourceType = ActivitySourceType.AzureDevOpsPullRequest,
            SettingsJson = SourceSettingsSerializer.Serialize(new AzureDevOpsSourceSettings
            {
                OrganizationUrl = "https://miniasp.visualstudio.com",
                Scopes = [scope]
            })
        };

        var batch = await new AzureDevOpsPullRequestSourceAdapter(new AzureDevOpsCliService(runner)).CollectAsync(
            new CollectionRequest(
                source,
                DateTimeOffset.Parse("2026-09-01T00:00:00+00:00"),
                DateTimeOffset.Parse("2026-09-05T00:00:00+00:00")),
            CancellationToken.None);

        Assert.Equal(1, batch.SuccessfulRepositories);
        Assert.Single(batch.Evidence);
        Assert.Contains("My PR", batch.Evidence[0].Title, StringComparison.Ordinal);
        Assert.Contains(batch.Warnings, warning => warning.Contains("以 az login 帳號", StringComparison.Ordinal));
        var prList = runner.Requests.Single(request =>
            string.Join(' ', request.Arguments).StartsWith("repos pr list ", StringComparison.Ordinal));
        Assert.Contains("--creator me@example.com", string.Join(' ', prList.Arguments), StringComparison.Ordinal);
    }

    [Fact]
    public void Web_url_helpers_do_not_cross_link_pr_and_work_item_details()
    {
        var pullRequestUrl = AzureDevOpsCliService.ToPullRequestWebUrl(
            "https://dev.azure.com/example/project-1/_workitems/edit/42",
            10,
            "https://dev.azure.com/example",
            "project-1",
            "Repo");
        var workItemUrl = AzureDevOpsCliService.ToWorkItemWebUrl(
            "https://dev.azure.com/example/project-1/_git/Repo/pullrequest/10",
            42,
            "https://dev.azure.com/example",
            "project-1");

        Assert.Equal(
            "https://dev.azure.com/example/project-1/_git/Repo/pullrequest/10",
            pullRequestUrl);
        Assert.Equal(
            "https://dev.azure.com/example/project-1/_workitems/edit/42",
            workItemUrl);
        Assert.Equal(
            "https://miniasp.visualstudio.com/project-1/_workitems/edit/42",
            AzureDevOpsCliService.ToWorkItemWebUrl(
                "https://miniasp.visualstudio.com/_apis/wit/workItems/42",
                42,
                "https://miniasp.visualstudio.com",
                "project-1"));
    }

    private static ProcessResult Json(string output) => new(0, output, string.Empty);

    private sealed class FakeProcessRunner(
        Func<ProcessRequest, string?, ProcessResult> handler) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            string? standardInput = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(handler(request, standardInput));
        }
    }
}
