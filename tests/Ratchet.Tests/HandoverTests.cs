using CodeStack.Ratchet.Core;
using CodeStack.Ratchet.Tests.Support;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// ADR-0004 ("loss is authored, never silent") at the generator boundary: a
/// truncated or empty handover must refuse loudly, never save quietly.
/// </summary>
public sealed class HandoverGeneratorTests
{
    private static Task<string> Generate(ScriptedLlmClient llm)
    {
        var convo = new Conversation();
        convo.Add(Message.UserText("we did some work"));
        return new HandoverGenerator(llm).GenerateAsync(convo, null, CancellationToken.None);
    }

    [Fact]
    public async Task CleanCompletion_ReturnsTheDocTrimmed()
    {
        var llm = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text("\n## Goal\nship it\n"));
        Assert.Equal("## Goal\nship it", await Generate(llm));
    }

    [Fact]
    public async Task MaxTokensTruncation_Throws_InsteadOfSavingASilentlyLossyDoc()
    {
        var llm = new ScriptedLlmClient()
            .Enqueue(ScriptedLlmClient.Text("## Goal\nship it\n## Current sta", stopReason: "max_tokens"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(llm));
        Assert.Contains("max_tokens", ex.Message);
    }

    [Fact]
    public async Task EmptyDoc_Throws()
    {
        var llm = new ScriptedLlmClient().Enqueue(ScriptedLlmClient.Text("   "));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Generate(llm));
    }
}
