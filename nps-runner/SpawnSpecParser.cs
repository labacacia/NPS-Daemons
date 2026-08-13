// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace NPS.Daemon.Runner;

/// <summary>
/// Parses both the CR-0007 portable OCI shape and the pre-alpha.17 direct
/// subprocess shape. Portable validation is fail closed.
/// </summary>
internal static class SpawnSpecParser
{
    public const string InvalidCode = "NOP-SPAWN-SPEC-INVALID";

    public static bool TryParse(string json, out SpawnSpec? spec, out string? errorCode)
    {
        spec = null;
        errorCode = InvalidCode;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var taskId = OptionalString(root, "task_id") ?? Guid.NewGuid().ToString("N");
            var replyTo = OptionalString(root, "reply_to");
            var idle = OptionalPositiveInt(root, "idle_timeout_seconds");
            var maximum = OptionalPositiveInt(root, "max_runtime_seconds");
            if (idle.Invalid || maximum.Invalid ||
                idle.Value is { } idleValue &&
                maximum.Value is { } maxValue &&
                idleValue > maxValue)
            {
                return false;
            }

            if (!TryReadEnvironment(root, out var environment))
                return false;

            if (root.TryGetProperty("image", out var imageElement))
            {
                if (imageElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(imageElement.GetString()) ||
                    !TryReadStringArray(root, "command", out var containerCommand, optional: true) ||
                    !TryReadResourceLimits(root, out var limits))
                {
                    return false;
                }

                spec = new SpawnSpec
                {
                    TaskId = taskId,
                    ReplyTo = replyTo,
                    Image = imageElement.GetString()!.Trim(),
                    ContainerCommand = containerCommand,
                    ResourceLimits = limits,
                    Env = environment,
                    IdleTimeoutSeconds = idle.Value,
                    MaxRuntimeSeconds = maximum.Value,
                };
                errorCode = null;
                return true;
            }

            // Compatibility ingress: direct host subprocess.
            var command = OptionalString(root, "command");
            if (string.IsNullOrWhiteSpace(command) ||
                !TryReadStringArray(root, "args", out var args, optional: true))
            {
                return false;
            }

            spec = new SpawnSpec
            {
                TaskId = taskId,
                ReplyTo = replyTo,
                Command = command,
                Args = args,
                WorkDir = OptionalString(root, "work_dir"),
                Env = environment,
                IdleTimeoutSeconds = idle.Value,
                MaxRuntimeSeconds = maximum.Value,
            };
            errorCode = null;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static (int? Value, bool Invalid) OptionalPositiveInt(
        JsonElement root,
        string name)
    {
        if (!root.TryGetProperty(name, out var element))
            return (null, false);
        if (!element.TryGetInt32(out var value) || value <= 0)
            return (null, true);
        return (value, false);
    }

    private static string? OptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static bool TryReadStringArray(
        JsonElement root,
        string name,
        out IReadOnlyList<string> values,
        bool optional)
    {
        values = Array.Empty<string>();
        if (!root.TryGetProperty(name, out var element))
            return optional;
        if (element.ValueKind != JsonValueKind.Array)
            return false;

        var result = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return false;
            result.Add(item.GetString()!);
        }
        values = result;
        return true;
    }

    private static bool TryReadEnvironment(
        JsonElement root,
        out IReadOnlyDictionary<string, string> environment)
    {
        environment = new Dictionary<string, string>();
        if (!root.TryGetProperty("env", out var element))
            return true;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(property.Name))
            {
                return false;
            }
            result[property.Name] = property.Value.GetString()!;
        }
        environment = result;
        return true;
    }

    private static bool TryReadResourceLimits(
        JsonElement root,
        out SpawnResourceLimits? limits)
    {
        limits = null;
        if (!root.TryGetProperty("resource_limits", out var element))
            return true;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var cpu = OptionalString(element, "cpu");
        var memory = OptionalString(element, "memory");
        long? cognBudget = null;
        if (element.TryGetProperty("cgn_budget", out var budgetElement))
        {
            if (!budgetElement.TryGetInt64(out var budget) || budget < 0)
                return false;
            cognBudget = budget;
        }

        if (element.TryGetProperty("cpu", out var cpuElement) &&
            cpuElement.ValueKind != JsonValueKind.String ||
            element.TryGetProperty("memory", out var memoryElement) &&
            memoryElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        limits = new SpawnResourceLimits(cpu, memory, cognBudget);
        return true;
    }
}
