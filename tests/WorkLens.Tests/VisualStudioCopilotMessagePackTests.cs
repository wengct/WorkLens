using System.Buffers.Binary;
using System.Text;
using WorkLens.Domain;
using WorkLens.Services;

namespace WorkLens.Tests;

public sealed class VisualStudioCopilotMessagePackTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        AppContext.BaseDirectory,
        "VisualStudioCopilotTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Validated_messagepack_session_is_imported_with_visible_chat_text_only()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sessionId = "chat-session-1";
        var created = DateTimeOffset.UtcNow.AddMinutes(-5);
        var updated = created.AddMinutes(2);
        var file = CreateSessionFile(
            sessionId,
            Fixture.Session(
                sessionId,
                created,
                updated,
                agentPreview: false,
                Fixture.UserEvent("u1", "整理登入流程"),
                Fixture.AssistantEvent(
                    "a1",
                    Fixture.ReasoningPart("這段思考不應匯入"),
                    Fixture.ToolPart("這段工具輸出不應匯入"),
                    Fixture.VisiblePart(3, "可見回覆"),
                    Fixture.VisiblePart(1, "```csharp\nreturn true;\n```"))));

        var source = CreateSource(file.solutionPath);
        var adapter = new VisualStudioCopilotSourceAdapter();

        var validation = await adapter.ValidateAsync(source, CancellationToken.None);
        var batch = await adapter.CollectAsync(
            new CollectionRequest(source, created.AddMinutes(-1)),
            CancellationToken.None);

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Details));
        Assert.Contains(validation.Details, detail => detail.Contains("可解析 1 個", StringComparison.Ordinal));
        var evidence = Assert.Single(batch.Evidence);
        var metadata = SourceSettingsSerializer.DeserializeCopilotMetadata(evidence.MetadataJson)!;

        Assert.Equal(EvidenceKind.CopilotSession, evidence.Kind);
        Assert.Equal(sessionId, metadata.SessionId);
        Assert.Equal("GitHub Copilot Chat", metadata.Client);
        Assert.Equal("visual-studio-copilot-msgpack", metadata.SourceFormat);
        Assert.True(metadata.IsComplete);
        Assert.Equal(["user", "assistant"], metadata.Messages.Select(message => message.Role).ToArray());
        Assert.Equal("整理登入流程", metadata.Messages[0].Text);
        Assert.Contains("可見回覆", metadata.Messages[1].Text, StringComparison.Ordinal);
        Assert.Contains("return true", metadata.Messages[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(metadata.Messages, message => message.Text.Contains("思考", StringComparison.Ordinal));
        Assert.DoesNotContain(metadata.Messages, message => message.Text.Contains("工具輸出", StringComparison.Ordinal));
        Assert.Equal(created.ToUnixTimeSeconds(), evidence.OccurredAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Agent_preview_responder_is_detected_from_the_selected_agent_service()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sessionId = "agent-preview-session";
        var created = DateTimeOffset.UtcNow.AddMinutes(-3);
        var file = CreateSessionFile(
            sessionId,
            Fixture.Session(
                sessionId,
                created,
                created.AddMinutes(1),
                agentPreview: true,
                Fixture.UserEvent("u1", "使用 Agent 檢查設定"),
                Fixture.AssistantEvent(
                    "a1",
                    Fixture.VisiblePart(3, "Agent 可見回覆"))));

        var batch = await new VisualStudioCopilotSourceAdapter().CollectAsync(
            new CollectionRequest(CreateSource(file.solutionPath), created.AddMinutes(-1)),
            CancellationToken.None);

        var evidence = Assert.Single(batch.Evidence);
        var metadata = SourceSettingsSerializer.DeserializeCopilotMetadata(evidence.MetadataJson)!;
        Assert.Equal("GitHub Copilot Agent (Preview)", metadata.Client);
        Assert.Contains("Agent 可見回覆", metadata.Messages[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_guid_update_imports_the_newer_complete_snapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sessionId = "chat-session-update";
        var created = DateTimeOffset.UtcNow.AddMinutes(-4);
        var initialFile = Fixture.Session(
            sessionId,
            created,
            created.AddSeconds(10),
            agentPreview: false,
            Fixture.UserEvent("u1", "第一個提示"),
            Fixture.AssistantEvent(
                "a1",
                Fixture.VisiblePart(3, "第一個回覆")));
        var file = CreateSessionFile(sessionId, initialFile);
        var source = CreateSource(file.solutionPath);
        var adapter = new VisualStudioCopilotSourceAdapter();

        var first = await adapter.CollectAsync(
            new CollectionRequest(source, created.AddMinutes(-1)),
            CancellationToken.None);
        var firstEvidence = Assert.Single(first.Evidence);
        source.CheckpointJson = first.CheckpointJson;
        source.LastSuccessAt = DateTimeOffset.UtcNow;

        var newer = DateTimeOffset.UtcNow;
        await File.WriteAllBytesAsync(
            file.filePath,
            Fixture.Session(
                sessionId,
                created,
                newer,
                agentPreview: false,
                Fixture.UserEvent("u1", "第一個提示"),
                Fixture.AssistantEvent(
                    "a1",
                    Fixture.VisiblePart(3, "第一個回覆")),
                Fixture.UserEvent("u2", "追加提示"),
                Fixture.AssistantEvent(
                    "a2",
                    Fixture.VisiblePart(3, "追加回覆"))));
        File.SetLastWriteTimeUtc(file.filePath, DateTime.UtcNow.AddSeconds(5));

        var second = await adapter.CollectAsync(
            new CollectionRequest(source, created.AddMinutes(-1)),
            CancellationToken.None);
        var secondEvidence = Assert.Single(second.Evidence);
        var metadata = SourceSettingsSerializer.DeserializeCopilotMetadata(secondEvidence.MetadataJson)!;

        Assert.Equal(firstEvidence.ExternalKey, secondEvidence.ExternalKey);
        Assert.True(metadata.IsComplete);
        Assert.Equal(4, metadata.Messages.Count);
        Assert.Contains("追加提示", secondEvidence.CommitMessage, StringComparison.Ordinal);
        Assert.Contains("追加回覆", metadata.Messages[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Truncated_messagepack_keeps_readable_messages_and_marks_snapshot_incomplete()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sessionId = "truncated-session";
        var created = DateTimeOffset.UtcNow.AddMinutes(-2);
        var completeBytes = Fixture.Session(
            sessionId,
            created,
            created,
            agentPreview: false,
            Fixture.UserEvent("u1", "截斷前的提示"),
            Fixture.AssistantEvent(
                "a1",
                Fixture.VisiblePart(3, "截斷前的回覆")));
        var file = CreateSessionFile(sessionId, completeBytes[..^3]);

        var batch = await new VisualStudioCopilotSourceAdapter().CollectAsync(
            new CollectionRequest(CreateSource(file.solutionPath), created.AddMinutes(-1)),
            CancellationToken.None);

        var evidence = Assert.Single(batch.Evidence);
        var metadata = SourceSettingsSerializer.DeserializeCopilotMetadata(evidence.MetadataJson)!;
        Assert.False(metadata.IsComplete);
        Assert.Contains(metadata.Messages, message => message.Text == "截斷前的提示");
        Assert.Contains(batch.Warnings, warning => warning.Contains("重試", StringComparison.Ordinal));
    }

    private (string solutionPath, string filePath) CreateSessionFile(string sessionId, byte[] bytes)
    {
        var solutionPath = Path.Combine(testRoot, "SampleSolution");
        var sessions = Path.Combine(
            solutionPath,
            ".vs",
            "SampleSolution",
            "copilot-chat",
            "profile",
            "sessions");
        Directory.CreateDirectory(sessions);
        var filePath = Path.Combine(sessions, sessionId);
        File.WriteAllBytes(filePath, bytes);
        return (solutionPath, filePath);
    }

    private ActivitySource CreateSource(string solutionPath) => new()
    {
        SourceType = ActivitySourceType.WindowsVisualStudioCopilot,
        SettingsJson = SourceSettingsSerializer.Serialize(
            new VisualStudioCopilotSourceSettings { SolutionPaths = [solutionPath] })
    };

    public void Dispose()
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static class Fixture
    {
        public static byte[] Session(
            string sessionId,
            DateTimeOffset created,
            DateTimeOffset updated,
            bool agentPreview,
            params byte[][] events)
        {
            var bytes = new List<byte>();
            bytes.AddRange(Integer(1));
            bytes.AddRange(Map(
                ("Id", Array(String(sessionId))),
                ("TimeCreated", Timestamp(created)),
                ("TimeUpdated", Timestamp(updated)),
                ("SelectedAgent", Map(
                    ("Service", Map(
                        ("Name", String(agentPreview
                            ? "Microsoft.VisualStudio.Copilot.CopilotCliResponder"
                            : "Microsoft.VisualStudio.Copilot.CopilotChatAgentProvider"))))))));
            foreach (var item in events)
            {
                bytes.AddRange(item);
            }

            return bytes.ToArray();
        }

        public static byte[] UserEvent(string id, string text) =>
            Event(0, id, VisiblePart(3, text));

        public static byte[] AssistantEvent(string id, params byte[][] parts) =>
            Event(1, id, parts);

        public static byte[] VisiblePart(int code, string text) =>
            Array(Integer(code), Map(
                ("Visibility", Integer(3)),
                ("Content", String(text))));

        public static byte[] ReasoningPart(string text) =>
            Array(Integer(10), Map(
                ("Visibility", Integer(3)),
                ("Content", String(text)),
                ("EncryptedContent", String("encrypted")),
                ("ReasoningTokenCount", Integer(1))));

        public static byte[] ToolPart(string text) =>
            Array(Integer(7), Map(
                ("Visibility", Integer(3)),
                ("Function", String("run")),
                ("Result", String(text))));

        private static byte[] Event(int code, string id, params byte[][] parts) =>
            Array(
                Integer(code),
                Map(
                    ("MessageId", String(id)),
                    ("Content", Array(parts))));

        private static byte[] Timestamp(DateTimeOffset value)
        {
            var bytes = new byte[10];
            bytes[0] = 0xd7;
            bytes[1] = 0xff;
            var seconds = checked((ulong)value.ToUnixTimeSeconds());
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(2), seconds);
            return bytes;
        }

        private static byte[] Map(params (string Key, byte[] Value)[] entries)
        {
            if (entries.Length > 15)
            {
                throw new ArgumentOutOfRangeException(nameof(entries));
            }

            var bytes = new List<byte> { (byte)(0x80 | entries.Length) };
            foreach (var (key, value) in entries)
            {
                bytes.AddRange(String(key));
                bytes.AddRange(value);
            }

            return bytes.ToArray();
        }

        private static byte[] Array(params byte[][] values)
        {
            if (values.Length > 15)
            {
                throw new ArgumentOutOfRangeException(nameof(values));
            }

            var bytes = new List<byte> { (byte)(0x90 | values.Length) };
            foreach (var value in values)
            {
                bytes.AddRange(value);
            }

            return bytes.ToArray();
        }

        private static byte[] Integer(int value) =>
            value is >= 0 and <= 127
                ? [(byte)value]
                : throw new ArgumentOutOfRangeException(nameof(value));

        private static byte[] String(string value)
        {
            var encoded = Encoding.UTF8.GetBytes(value);
            if (encoded.Length <= 31)
            {
                return [(byte)(0xa0 | encoded.Length), .. encoded];
            }

            if (encoded.Length <= byte.MaxValue)
            {
                return [0xd9, (byte)encoded.Length, .. encoded];
            }

            throw new ArgumentOutOfRangeException(nameof(value), "測試字串不可超過 255 bytes。");
        }
    }
}
