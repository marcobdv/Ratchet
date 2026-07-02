using CodeStack.Ratchet.Core;
using Xunit;

namespace CodeStack.Ratchet.Tests;

public sealed class SessionTreeTests
{
    private static Message Assistant(string text) =>
        new(Role.Assistant, new ContentBlock[] { new TextBlock(text) });

    [Fact]
    public void Append_AdvancesHead_AndMaterializesRootToHead()
    {
        var t = new SessionTree();
        t.Append(Message.UserText("one"));
        t.Append(Assistant("two"));
        var head = t.Append(Message.UserText("three"));

        Assert.Equal(head, t.HeadId);
        var convo = t.MaterializeConversation();
        Assert.Equal(3, convo.Messages.Count);
        Assert.Equal("one", ((TextBlock)convo.Messages[0].Content[0]).Text);
        Assert.Equal("three", ((TextBlock)convo.Messages[2].Content[0]).Text);
    }

    [Fact]
    public void RewindOneTurn_ThenContinue_ForksABranch_AndPreservesOldNodes()
    {
        var t = new SessionTree();
        t.Append(Message.UserText("prompt 1"));
        t.Append(Assistant("answer 1"));
        t.Append(Message.UserText("prompt 2"));
        var oldTip = t.Append(Assistant("answer 2"));

        t.RewindTurns(1); // back before "prompt 2"

        var convo = t.MaterializeConversation();
        Assert.Equal(2, convo.Messages.Count); // prompt 1 + answer 1

        t.Append(Message.UserText("prompt 2b"));
        Assert.Equal(5, t.Count);                       // nothing destroyed
        Assert.True(t.Nodes.ContainsKey(oldTip));       // old branch still in the tree
    }

    [Fact]
    public void RewindTurns_TreatsToolResultUserMessages_AsPartOfTheTurn()
    {
        var t = new SessionTree();
        t.Append(Message.UserText("prompt"));
        t.Append(new Message(Role.Assistant, new ContentBlock[] { new ToolUseBlock("t1", "read", "{}") }));
        t.Append(Message.UserToolResults(new ContentBlock[] { new ToolResultBlock("t1", "data", false) }));
        t.Append(Assistant("final"));

        t.RewindTurns(1);

        // The whole turn (prompt + tool round-trip + answer) is gone; a tool-result
        // user message is NOT a turn boundary.
        Assert.Null(t.HeadId);
    }

    [Fact]
    public void RewindPastEverything_LeavesEmptyHead()
    {
        var t = new SessionTree();
        t.Append(Message.UserText("only"));
        t.RewindTurns(5);
        Assert.Null(t.HeadId);
        Assert.Empty(t.MaterializeConversation().Messages);
    }

    [Fact]
    public void Goto_MovesHead_AndRejectsUnknownNodes()
    {
        var t = new SessionTree();
        var first = t.Append(Message.UserText("one"));
        t.Append(Assistant("two"));

        Assert.True(t.Goto(first));
        Assert.Equal(first, t.HeadId);
        Assert.False(t.Goto("nope"));
        Assert.Equal(first, t.HeadId);
    }

    [Fact]
    public void Goto_RejectsMidTurnToolUseNodes()
    {
        // Landing HEAD on an assistant message with tool_use and then prompting would
        // leave the tool_use unanswered — the same poisoning RewindTurns guards against.
        var t = new SessionTree();
        t.Append(Message.UserText("prompt"));
        var midTurn = t.Append(new Message(Role.Assistant, new ContentBlock[] { new ToolUseBlock("t1", "read", "{}") }));
        var results = t.Append(Message.UserToolResults(new ContentBlock[] { new ToolResultBlock("t1", "data", false) }));
        var tail = t.Append(Assistant("done"));

        Assert.False(t.Goto(midTurn));
        Assert.Equal(tail, t.HeadId);       // head unmoved on rejection
        Assert.True(t.Goto(results));       // a tool-result node answers its tool_use — valid
    }

    [Fact]
    public void RewindTurns_ZeroOrNegative_IsANoOp_NotAWipe()
    {
        var t = new SessionTree();
        t.Append(Message.UserText("prompt"));
        var head = t.Append(Assistant("answer"));

        t.RewindTurns(0);
        Assert.Equal(head, t.HeadId);
        t.RewindTurns(-3);
        Assert.Equal(head, t.HeadId);
    }

    [Fact]
    public void FromNodes_Roundtrip_PreservesPathAndContinuesCounter()
    {
        var t = new SessionTree();
        t.Append(Message.UserText("one"));
        var head = t.Append(Assistant("two"));

        var restored = SessionTree.FromNodes(t.Nodes.Values, head);
        Assert.Equal(head, restored.HeadId);
        Assert.Equal(2, restored.MaterializeConversation().Messages.Count);

        var next = restored.Append(Message.UserText("three"));
        Assert.Equal("3", next); // counter continued, no id collision
    }

    [Fact]
    public void FromNodes_RejectsDanglingParents_AtLoadTime()
    {
        Assert.Throws<InvalidDataException>(() => SessionTree.FromNodes(
            new[] { new SessionTree.Node("1", "99", Message.UserText("x")) }, head: "1"));
    }

    [Fact]
    public void FromNodes_RejectsDanglingHead_AndParentCycles()
    {
        Assert.Throws<InvalidDataException>(() => SessionTree.FromNodes(
            new[] { new SessionTree.Node("1", null, Message.UserText("x")) }, head: "42"));

        Assert.Throws<InvalidDataException>(() => SessionTree.FromNodes(
            new[]
            {
                new SessionTree.Node("1", "2", Message.UserText("a")),
                new SessionTree.Node("2", "1", Message.UserText("b")),
            }, head: "1"));
    }
}

public sealed class SessionIdTests
{
    [Theory]
    [InlineData("20260702-120000-000-abcd", true)]
    [InlineData("simple", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("../escape", false)]
    [InlineData("a/b", false)]
    [InlineData(@"a\b", false)]
    [InlineData("a..b", false)]
    [InlineData(@"C:\rooted", false)]
    public void IsValid_RejectsPathEscapes(string id, bool expected) =>
        Assert.Equal(expected, SessionId.IsValid(id));

    [Fact]
    public void NewId_IsUniqueAcrossQuickSuccession()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => SessionId.NewId()).ToHashSet();
        Assert.Equal(50, ids.Count);
        Assert.All(ids, id => Assert.True(SessionId.IsValid(id)));
    }
}

public sealed class FileSessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ratchet-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private FileSessionStore MakeStore() => new(_dir);

    [Fact]
    public void SaveLoad_RoundtripsAllFourBlockShapes_HeadAndBranches()
    {
        var t = new SessionTree();
        t.Append(Message.UserText("prompt"));
        t.Append(new Message(Role.Assistant, new ContentBlock[]
        {
            new TextBlock("thinking aloud"),
            new ToolUseBlock("toolu_1", "read", """{"path":"a.txt"}"""),
        }));
        t.Append(Message.UserToolResults(new ContentBlock[] { new ToolResultBlock("toolu_1", "file contents", false) }));
        var head = t.Append(new Message(Role.Assistant, new ContentBlock[] { new TextBlock("done") }));
        t.RewindTurns(1);
        t.Append(Message.UserText("alternate prompt")); // a second branch

        var store = MakeStore();
        var id = store.Save(null, t);
        var loaded = store.Load(id);

        Assert.NotNull(loaded);
        Assert.Equal(t.Count, loaded!.Count);
        Assert.Equal(t.HeadId, loaded.HeadId);

        // The abandoned branch survives the roundtrip too.
        Assert.True(loaded.Goto(head));
        var convo = loaded.MaterializeConversation();
        Assert.Equal(4, convo.Messages.Count);

        var toolUse = Assert.IsType<ToolUseBlock>(convo.Messages[1].Content[1]);
        Assert.Equal("toolu_1", toolUse.Id);
        Assert.Equal("read", toolUse.Name);
        // The store re-serializes InputJson (indentation may differ); compare structurally.
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse("""{"path":"a.txt"}"""),
            System.Text.Json.Nodes.JsonNode.Parse(toolUse.InputJson)));

        var result = Assert.IsType<ToolResultBlock>(convo.Messages[2].Content[0]);
        Assert.Equal("toolu_1", result.ToolUseId);
        Assert.Equal("file contents", result.Content);
        Assert.False(result.IsError);
    }

    [Fact]
    public void Load_UnknownOrUnsafeId_ReturnsNull()
    {
        var store = MakeStore();
        Assert.Null(store.Load("does-not-exist"));
        Assert.Null(store.Load("../../etc/passwd"));
    }

    [Fact]
    public void List_ReturnsMostRecentFirst_AndSkipsMalformedFiles()
    {
        var store = MakeStore();
        var t1 = new SessionTree();
        t1.Append(Message.UserText("first session prompt"));
        store.Save("session-a", t1);

        // A malformed file in the directory must not break listing.
        File.WriteAllText(Path.Combine(_dir, ".ratchet", "sessions", "broken.json"), "{ not json");

        var t2 = new SessionTree();
        t2.Append(Message.UserText("second session prompt"));
        store.Save("session-b", t2);

        var infos = store.List();
        Assert.Equal(2, infos.Count);
        Assert.Contains("second session prompt", infos[0].Preview);
    }

    [Fact]
    public void Load_V02FlatMessagesFormat_ImportsAsLinearChain()
    {
        var store = MakeStore();
        var path = Path.Combine(_dir, ".ratchet", "sessions", "legacy.json");
        File.WriteAllText(path, """
        {
          "id": "legacy",
          "messages": [
            { "role": "user", "content": [ { "type": "text", "text": "old prompt" } ] },
            { "role": "assistant", "content": [ { "type": "text", "text": "old answer" } ] }
          ]
        }
        """);

        var loaded = store.Load("legacy");
        Assert.NotNull(loaded);
        var convo = loaded!.MaterializeConversation();
        Assert.Equal(2, convo.Messages.Count);
        Assert.Equal("old answer", ((TextBlock)convo.Messages[1].Content[0]).Text);
    }

    [Fact]
    public void Load_UnrecognizedSchema_RefusesLoudly_NeverAnEmptyTree()
    {
        var store = MakeStore();
        var path = Path.Combine(_dir, ".ratchet", "sessions", "odd.json");
        File.WriteAllText(path, """{ "someFutureShape": true }""");

        Assert.Throws<InvalidDataException>(() => store.Load("odd"));
    }

    [Fact]
    public void Load_TruncatedJson_RefusesWithAClearError()
    {
        var store = MakeStore();
        var path = Path.Combine(_dir, ".ratchet", "sessions", "cut.json");
        File.WriteAllText(path, """{ "id": "cut", "nodes": [ { "id": "1", "par"""); // crash mid-write

        var ex = Assert.Throws<InvalidDataException>(() => store.Load("cut"));
        Assert.Contains("cut.json", ex.Message);
    }

    [Fact]
    public void Save_LeavesNoTempFileBehind_AndListIgnoresStrays()
    {
        var store = MakeStore();
        var t = new SessionTree();
        t.Append(Message.UserText("hello"));
        var id = store.Save(null, t);
        store.Save(id, t); // second save exercises the overwrite path

        var dir = Path.Combine(_dir, ".ratchet", "sessions");
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Assert.Single(store.List());
    }
}
