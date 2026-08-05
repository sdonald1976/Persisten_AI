using Companion.Core.Abstractions;
using Companion.Infrastructure;
using Companion.Infrastructure.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Companion.Tests;

public class ModelSelectionTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCompanion(config, "Data Source=file:model-test?mode=memory&cache=shared");
        return services.BuildServiceProvider();
    }

    private static string ChatModelFor(IServiceProvider sp, string? role)
    {
        var chat = role is null
            ? sp.GetRequiredService<IChatModel>()
            : sp.GetRequiredKeyedService<IChatModel>(role);
        return ((OpenAiCompatibleChatModel)chat).ModelName;
    }

    [Fact]
    public void EachJob_UsesItsOwnConfiguredModel()
    {
        using var sp = Build(new Dictionary<string, string?>
        {
            ["Models:Provider"] = "OpenAiCompatible",
            ["Models:Chat:BaseUrl"] = "http://localhost:11434/v1",
            ["Models:Chat:Model"] = "big-conversational",
            ["Models:Extraction:BaseUrl"] = "http://localhost:11434/v1",
            ["Models:Extraction:Model"] = "small-extractor",
            ["Models:Summarizer:BaseUrl"] = "http://localhost:11434/v1",
            ["Models:Summarizer:Model"] = "fast-summarizer",
            ["Models:Embeddings:BaseUrl"] = "http://localhost:11434/v1",
            ["Models:Embeddings:Model"] = "dedicated-embedder",
        });

        Assert.Equal("big-conversational", ChatModelFor(sp, null));            // default reply model
        Assert.Equal("big-conversational", ChatModelFor(sp, "conversation"));
        Assert.Equal("small-extractor", ChatModelFor(sp, "extraction"));
        Assert.Equal("fast-summarizer", ChatModelFor(sp, "summarizer"));

        var embed = (OpenAiCompatibleEmbeddingModel)sp.GetRequiredService<IEmbeddingModel>();
        Assert.Equal("dedicated-embedder", embed.ModelName);
    }

    [Fact]
    public void UnconfiguredJobs_FallBackToTheConversationalModel()
    {
        using var sp = Build(new Dictionary<string, string?>
        {
            ["Models:Provider"] = "OpenAiCompatible",
            ["Models:Chat:BaseUrl"] = "http://localhost:11434/v1",
            ["Models:Chat:Model"] = "only-model",
            ["Models:Embeddings:BaseUrl"] = "http://localhost:11434/v1",
            ["Models:Embeddings:Model"] = "embedder",
        });

        // No Extraction/Summarizer sections → they reuse the conversational model.
        Assert.Equal("only-model", ChatModelFor(sp, "extraction"));
        Assert.Equal("only-model", ChatModelFor(sp, "summarizer"));
    }

    [Fact]
    public void MockProvider_IsUsed_WhenNotConfigured()
    {
        using var sp = Build(new Dictionary<string, string?>());
        // Default provider is Mock, so the real adapter is not registered.
        Assert.IsNotType<OpenAiCompatibleChatModel>(sp.GetRequiredService<IChatModel>());
        Assert.IsType<RuleBasedMemoryExtractor>(sp.GetRequiredService<IMemoryExtractor>());
        // Optional multimodal models are off by default.
        Assert.Null(sp.GetService<IVisionModel>());
        Assert.Null(sp.GetService<ITranscriber>());
    }

    [Fact]
    public void Vision_And_Transcription_AreRegistered_OnlyWhenConfigured()
    {
        using var withBoth = Build(new Dictionary<string, string?>
        {
            ["Models:Provider"] = "OpenAiCompatible",
            ["Models:Chat:Model"] = "chat",
            ["Models:Embeddings:Model"] = "embed",
            ["Models:Vision:BaseUrl"] = "http://localhost:11434/v1",
            ["Models:Vision:Model"] = "llama3.2-vision",
            ["Models:Transcription:BaseUrl"] = "http://localhost:9000/v1",
            ["Models:Transcription:Model"] = "whisper-1",
        });
        Assert.Equal("llama3.2-vision",
            ((OpenAiCompatibleVisionModel)withBoth.GetRequiredService<IVisionModel>()).ModelName);
        Assert.NotNull(withBoth.GetService<ITranscriber>());

        // Real provider, but no Vision/Transcription sections → not registered.
        using var withoutMultimodal = Build(new Dictionary<string, string?>
        {
            ["Models:Provider"] = "OpenAiCompatible",
            ["Models:Chat:Model"] = "chat",
            ["Models:Embeddings:Model"] = "embed",
        });
        Assert.Null(withoutMultimodal.GetService<IVisionModel>());
        Assert.Null(withoutMultimodal.GetService<ITranscriber>());
    }
}
