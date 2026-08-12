using Companion.Core.Abstractions;
using Companion.Core.Services;
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
        // Every role is wrapped in call telemetry; unwrap to assert the configured adapter.
        var inner = chat is LoggingChatModel logging ? logging.Inner : chat;
        return ((OpenAiCompatibleChatModel)inner).ModelName;
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
        Assert.Equal("fast-summarizer", ChatModelFor(sp, "reranker"));
        Assert.Equal("small-extractor", ChatModelFor(sp, "safety"));
        Assert.Equal("fast-summarizer", ChatModelFor(sp, "task-auditor"));

        var embed = (OpenAiCompatibleEmbeddingModel)((LoggingEmbeddingModel)sp.GetRequiredService<IEmbeddingModel>()).Inner;
        Assert.Equal("dedicated-embedder", embed.ModelName);
    }

    [Fact]
    public void SamplingOptions_BindFromConfiguration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Models:Provider"] = "OpenAiCompatible",
            ["Models:Chat:Model"] = "chat",
            ["Models:Chat:Temperature"] = "0.4",
            ["Models:Chat:MaxTokens"] = "512",
        }).Build();

        var options = config.GetSection(ModelOptions.SectionName).Get<ModelOptions>()!;

        Assert.Equal(0.4, options.Chat.Temperature);
        Assert.Equal(512, options.Chat.MaxTokens);
        Assert.Null(options.Embeddings.Temperature); // unset stays null (use server default)
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
        Assert.Equal("only-model", ChatModelFor(sp, "reranker"));
        Assert.Equal("only-model", ChatModelFor(sp, "safety"));
        Assert.Equal("only-model", ChatModelFor(sp, "task-auditor"));
    }

    [Fact]
    public void MockProvider_IsUsed_WhenNotConfigured()
    {
        using var sp = Build(new Dictionary<string, string?>());
        // Default provider is Mock, so the real adapter is not registered.
        Assert.IsNotType<OpenAiCompatibleChatModel>(sp.GetRequiredService<IChatModel>());
        Assert.IsType<RuleBasedMemoryExtractor>(sp.GetRequiredService<IMemoryExtractor>());
        Assert.IsType<RuleBasedMemoryReranker>(sp.GetRequiredService<IMemoryReranker>());
        Assert.IsType<RuleBasedPrivacyClassifier>(sp.GetRequiredService<IPrivacyClassifier>());
        // Optional multimodal models are off by default.
        Assert.Null(sp.GetService<IVisionModel>());
        Assert.Null(sp.GetService<ITranscriber>());
    }

    [Fact]
    public void Vision_Transcription_AndSpeech_AreRegistered_OnlyWhenConfigured()
    {
        using var withAll = Build(new Dictionary<string, string?>
        {
            ["Models:Provider"] = "OpenAiCompatible",
            ["Models:Chat:Model"] = "chat",
            ["Models:Embeddings:Model"] = "embed",
            ["Models:Vision:BaseUrl"] = "http://localhost:11434/v1",
            ["Models:Vision:Model"] = "llama3.2-vision",
            ["Models:Transcription:BaseUrl"] = "http://localhost:9000/v1",
            ["Models:Transcription:Model"] = "whisper-1",
            ["Models:Speech:BaseUrl"] = "http://localhost:8080/v1",
            ["Models:Speech:Model"] = "tts-1",
        });
        Assert.Equal("llama3.2-vision",
            ((OpenAiCompatibleVisionModel)withAll.GetRequiredService<IVisionModel>()).ModelName);
        Assert.NotNull(withAll.GetService<ITranscriber>());
        Assert.NotNull(withAll.GetService<ISpeechSynthesizer>());

        // Real provider, but no Vision/Transcription/Speech sections → not registered.
        using var withoutMultimodal = Build(new Dictionary<string, string?>
        {
            ["Models:Provider"] = "OpenAiCompatible",
            ["Models:Chat:Model"] = "chat",
            ["Models:Embeddings:Model"] = "embed",
        });
        Assert.Null(withoutMultimodal.GetService<IVisionModel>());
        Assert.Null(withoutMultimodal.GetService<ITranscriber>());
        Assert.Null(withoutMultimodal.GetService<ISpeechSynthesizer>());
    }
}
