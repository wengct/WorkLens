using System.Globalization;
using WorkLens.Domain;

namespace WorkLens.Services;

internal sealed class VisualStudioMessagePackParseResult
{
    public VisualStudioMessagePackParseResult(
        IReadOnlyList<VisualStudioMessagePackValue> values,
        VisualStudioMessagePackValue? root,
        bool isComplete,
        List<string> diagnostics)
    {
        Values = values;
        Root = root;
        IsComplete = isComplete;
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<VisualStudioMessagePackValue> Values { get; }
    public VisualStudioMessagePackValue? Root { get; }
    public bool IsComplete { get; set; }
    public List<string> Diagnostics { get; }
}

internal static class VisualStudioMessagePackParser
{
    public static VisualStudioMessagePackParseResult Parse(byte[] bytes)
    {
        var reader = new VisualStudioMessagePackReader(bytes);
        var values = new List<VisualStudioMessagePackValue>();
        var diagnostics = new List<string>();
        var complete = true;
        while (!reader.End)
        {
            try
            {
                values.Add(reader.ReadValue());
            }
            catch (VisualStudioMessagePackFormatException exception)
            {
                complete = false;
                diagnostics.Add($"MessagePack 內容不完整：{exception.Message}");
                break;
            }
        }

        var root = values.FirstOrDefault(value =>
            value.IsMap &&
            (value.Map!.ContainsKey("TimeCreated") || value.Map.ContainsKey("TimeUpdated")));
        if (root is null && values.Count > 1 && values[1].IsMap)
        {
            root = values[1];
        }

        return new VisualStudioMessagePackParseResult(values, root, complete, diagnostics);
    }

    public static string? GetSessionId(VisualStudioMessagePackValue root)
    {
        if (!root.TryGetMapValue("Id", out var id))
        {
            return null;
        }

        if (id.IsArray)
        {
            return id.Array!
                .Select(AsString)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        return AsString(id);
    }

    public static DateTimeOffset? GetTimestamp(
        VisualStudioMessagePackValue root,
        string name)
    {
        return root.TryGetMapValue(name, out var value)
            ? AsTimestamp(value)
            : null;
    }

    public static bool IsAgentPreview(VisualStudioMessagePackValue root)
    {
        if (root.TryGetMapValue("SelectedAgent", out var selectedAgent) &&
            ContainsString(selectedAgent, "CopilotCliResponder"))
        {
            return true;
        }

        return root.TryGetMapValue("Responders", out var responders) &&
               ContainsString(responders, "CopilotCliResponder");
    }

    public static List<CopilotSessionMessage> ExtractMessages(
        IReadOnlyList<VisualStudioMessagePackValue> values,
        DateTimeOffset fallbackTimestamp,
        ICollection<string> diagnostics)
    {
        var messages = new List<CopilotSessionMessage>();
        var ordinal = 0;
        foreach (var value in values)
        {
            if (!value.IsArray ||
                value.Array!.Count < 2 ||
                !TryGetInt(value.Array[0], out var eventCode) ||
                !value.Array[1].IsMap)
            {
                continue;
            }

            if (eventCode is not 0 and not 1)
            {
                diagnostics.Add($"略過未知的 Copilot session event 類型：{eventCode}。");
                continue;
            }

            ordinal++;
            var eventMap = value.Array[1];
            var role = eventCode == 0 ? "user" : "assistant";
            var text = ReadVisibleText(eventMap, diagnostics);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var id = eventMap.TryGetMapValue("MessageId", out var messageId)
                ? AsString(messageId)
                : null;
            messages.Add(new CopilotSessionMessage
            {
                Id = string.IsNullOrWhiteSpace(id) ? $"{role}:{ordinal}" : id,
                Role = role,
                Timestamp = fallbackTimestamp,
                Text = text
            });
        }

        return messages;
    }

    private static string ReadVisibleText(
        VisualStudioMessagePackValue eventMap,
        ICollection<string> diagnostics)
    {
        if (!eventMap.TryGetMapValue("Content", out var content))
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (content.IsArray)
        {
            foreach (var item in content.Array!)
            {
                if (!item.IsArray ||
                    item.Array!.Count < 2 ||
                    !TryGetInt(item.Array[0], out var partCode) ||
                    !item.Array[1].IsMap)
                {
                    diagnostics.Add("略過無法辨識的 Visual Studio Copilot Content 項目。");
                    continue;
                }

                var part = item.Array[1];
                if (!IsVisiblePart(partCode, part) ||
                    !part.TryGetMapValue("Content", out var textValue) ||
                    textValue.Kind != VisualStudioMessagePackValueKind.String ||
                    string.IsNullOrWhiteSpace(textValue.StringValue))
                {
                    continue;
                }

                parts.Add(textValue.StringValue!);
            }
        }
        else if (content.IsMap &&
                 IsVisiblePart(3, content) &&
                 content.TryGetMapValue("Content", out var textValue) &&
                 textValue.Kind == VisualStudioMessagePackValueKind.String)
        {
            parts.Add(textValue.StringValue ?? string.Empty);
        }

        return string.Join(
            Environment.NewLine,
            parts.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static bool IsVisiblePart(
        long partCode,
        VisualStudioMessagePackValue part)
    {
        // In the validated VS 2026 stream, 1 and 3 are visible markdown/code
        // blocks, 7 is a tool invocation, and 10 contains reasoning text and
        // encrypted reasoning content.
        if (partCode is not 1 and not 3)
        {
            return false;
        }

        if (part.Map!.ContainsKey("EncryptedContent") ||
            part.Map.ContainsKey("ReasoningTokenCount") ||
            part.Map.ContainsKey("ThinkingElapsedMs") ||
            part.Map.ContainsKey("Function") ||
            part.Map.ContainsKey("Result") ||
            part.Map.ContainsKey("CallGroupIdentifier") ||
            part.Map.ContainsKey("Confirmation") ||
            part.Map.ContainsKey("AdditionalResultContext"))
        {
            return false;
        }

        if (part.Map.TryGetValue("Visibility", out var visibility))
        {
            if (TryGetInt(visibility, out var numericVisibility) && numericVisibility != 3)
            {
                return false;
            }

            if (visibility.Kind == VisualStudioMessagePackValueKind.String &&
                visibility.StringValue is { } marker &&
                marker.Contains("hidden", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsString(
        VisualStudioMessagePackValue value,
        string text)
    {
        if (value.Kind == VisualStudioMessagePackValueKind.String)
        {
            return value.StringValue?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
        }

        if (value.IsArray)
        {
            return value.Array!.Any(item => ContainsString(item, text));
        }

        return value.IsMap && value.Map!.Any(item => ContainsString(item.Value, text));
    }

    private static string? AsString(VisualStudioMessagePackValue value) =>
        value.Kind == VisualStudioMessagePackValueKind.String
            ? value.StringValue
            : null;

    private static DateTimeOffset? AsTimestamp(VisualStudioMessagePackValue value)
    {
        if (value.Kind == VisualStudioMessagePackValueKind.Timestamp)
        {
            return value.Timestamp;
        }

        if (value.Kind == VisualStudioMessagePackValueKind.String &&
            DateTimeOffset.TryParse(
                value.StringValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return parsed;
        }

        if (TryGetInt(value, out var milliseconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    private static bool TryGetInt(
        VisualStudioMessagePackValue value,
        out long result)
    {
        if (value.Kind != VisualStudioMessagePackValueKind.Integer)
        {
            result = default;
            return false;
        }

        if (value.IsUnsigned)
        {
            if (value.UnsignedInteger > long.MaxValue)
            {
                result = default;
                return false;
            }

            result = (long)value.UnsignedInteger;
            return true;
        }

        result = value.Integer;
        return true;
    }
}
