using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Companion.Core.Abstractions;
using Companion.Core.Domain;
using Companion.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// The curation endpoints over the real app: merging folds one project into another (and the
/// old name keeps resolving via its alias); malformed requests are rejected before any work.
/// </summary>
public class ProjectCurationApiTests : IClassFixture<CompanionApiFactory>
{
    private readonly CompanionApiFactory _factory;

    public ProjectCurationApiTests(CompanionApiFactory factory) => _factory = factory;

    private HttpClient Authed()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CompanionApiFactory.Token);
        return client;
    }

    private async Task<(Guid SourceId, Guid TargetId, string UserId)> SeedTwoProjectsAsync(string prefix)
    {
        using var scope = _factory.Services.CreateScope();
        var user = scope.ServiceProvider.GetRequiredService<IUserContext>().UserId;
        var store = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        var now = DateTimeOffset.UtcNow;
        var source = new Project
        {
            Id = Guid.NewGuid(), UserId = user, Name = $"{prefix} sensor board",
            CreatedAt = now, LastActivityAt = now,
        };
        var target = new Project
        {
            Id = Guid.NewGuid(), UserId = user, Name = $"{prefix} automation",
            CreatedAt = now, LastActivityAt = now,
        };
        await store.AddProjectAsync(source);
        await store.AddProjectAsync(target);
        return (source.Id, target.Id, user);
    }

    [Fact]
    public async Task Merge_FoldsSourceIntoTarget_AndKeepsTheOldNameAsAlias()
    {
        var (sourceId, targetId, user) = await SeedTwoProjectsAsync("Aviary");
        using var client = Authed();

        var response = await client.PostAsJsonAsync("/projects/merge",
            new { sourceId = sourceId.ToString(), targetId = targetId.ToString() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        Assert.Null(await store.GetProjectAsync(sourceId, user));
        var aliases = await store.GetAliasesByProjectAsync(user, targetId);
        Assert.Contains(aliases, a => a.Alias == "Aviary sensor board");
    }

    [Fact]
    public async Task Merge_WithMalformedIds_IsRejected()
    {
        using var client = Authed();
        var response = await client.PostAsJsonAsync("/projects/merge",
            new { sourceId = "not-a-guid", targetId = "also-no" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Merge_OfAProjectIntoItself_IsRejected()
    {
        var (sourceId, _, _) = await SeedTwoProjectsAsync("Selfmerge");
        using var client = Authed();
        var response = await client.PostAsJsonAsync("/projects/merge",
            new { sourceId = sourceId.ToString(), targetId = sourceId.ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Split_CreatesTheNewProject()
    {
        var (sourceId, _, user) = await SeedTwoProjectsAsync("Splitting");
        using var client = Authed();

        var response = await client.PostAsJsonAsync("/projects/split",
            new { sourceId = sourceId.ToString(), newName = "Splitting irrigation" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        var projects = await store.GetProjectsAsync(user);
        Assert.Contains(projects, p => p.Name == "Splitting irrigation");
    }

    [Fact]
    public async Task Split_OfAMissingProject_Is404()
    {
        using var client = Authed();
        var response = await client.PostAsJsonAsync("/projects/split",
            new { sourceId = Guid.NewGuid().ToString(), newName = "Ghost project" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
