using System.Globalization;
using VeilGate.Domain.Detection;
using VeilGate.Domain.Policies;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace VeilGate.Application.Policies;

public sealed record PolicyIssue(int Line, int Column, string Message);

public sealed record PolicyParseResult(PolicyDocument? Policy, IReadOnlyList<PolicyIssue> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Policy is not null && Errors.Count == 0;
}

public static class PolicyParser
{
    private const int MaxYamlChars = 20_000;
    private static readonly string[] TopLevel = ["version", "name", "defaults", "entities", "reidentify", "output", "limits"];

    public static PolicyParseResult Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return Failed(1, 1, "Policy is empty.");
        if (yaml.Length > MaxYamlChars) return Failed(1, 1, $"Policy is longer than {MaxYamlChars} characters.");

        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode map)
                return Failed(1, 1, "A policy must be one YAML mapping.");
            root = map;
        }
        catch (YamlException ex)
        {
            return Failed((int)ex.Start.Line, (int)ex.Start.Column, ex.InnerException?.Message ?? ex.Message);
        }

        var errors = new List<PolicyIssue>();
        var warnings = new List<string>();

        foreach (var key in root.Children.Keys.OfType<YamlScalarNode>())
            if (!TopLevel.Contains(key.Value)) AddError(errors, key, $"Unknown setting '{key.Value}'.");

        var version = Scalar(root, "version", errors, required: true);
        if (version is not null && version.Value != "1") AddError(errors, version, "Only version 1 is supported.");

        var nameNode = Scalar(root, "name", errors, required: true);
        var name = nameNode?.Value?.Trim() ?? string.Empty;
        if (nameNode is not null && name.Length is < 1 or > 80) AddError(errors, nameNode, "Name must be 1 to 80 characters.");

        // defaults
        var defaultAction = PolicyAction.Redact;
        var minConfidence = 0.5;
        var defaultsMap = Section(root, "defaults", ["action", "minConfidence"], errors);
        if (defaultsMap is not null)
        {
            var a = Scalar(defaultsMap, "action", errors);
            if (a is not null) defaultAction = ReadAction(a, errors) ?? defaultAction;

            var c = Scalar(defaultsMap, "minConfidence", errors);
            if (c is not null)
            {
                if (double.TryParse(c.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 1) minConfidence = v;
                else AddError(errors, c, "minConfidence must be a number from 0 to 1.");
            }
        }

        // entities
        var entities = new Dictionary<string, EntityRule>();
        var entitiesMap = Section(root, "entities", null, errors);
        if (entitiesMap is not null)
        {
            foreach (var (keyNode, valueNode) in entitiesMap.Children)
            {
                if (keyNode is not YamlScalarNode k || string.IsNullOrEmpty(k.Value)) { AddError(errors, keyNode, "Entity type must be text."); continue; }
                var type = k.Value;
                if (!EntityTypes.IsValid(type)) { AddError(errors, k, $"Unknown entity type '{type}'."); continue; }
                if (valueNode is not YamlMappingNode ruleMap) { AddError(errors, valueNode, $"'{type}' needs settings like {{ action: MASK }}."); continue; }

                foreach (var rk in ruleMap.Children.Keys.OfType<YamlScalarNode>())
                    if (rk.Value is not ("action" or "maxDays")) AddError(errors, rk, $"Unknown setting '{rk.Value}'.");

                var actionNode = Scalar(ruleMap, "action", errors, required: true);
                if (actionNode is null) continue;
                var action = ReadAction(actionNode, errors);
                if (action is null) continue;

                var maxDays = 365;
                var md = Scalar(ruleMap, "maxDays", errors);
                if (md is not null)
                {
                    if (action != PolicyAction.Shift) AddError(errors, md, "maxDays only applies to SHIFT.");
                    else if (int.TryParse(md.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 365) maxDays = days;
                    else AddError(errors, md, "maxDays must be a whole number from 1 to 365.");
                }

                if (action == PolicyAction.Shift && type is not (EntityTypes.Date or EntityTypes.Dob))
                    AddError(errors, actionNode, "SHIFT only works for DATE and DOB.");
                if (action == PolicyAction.Allow && EntityTypes.Known.Contains(type))
                    warnings.Add($"ALLOW on {type} leaves a HIPAA identifier unprotected (high risk).");

                entities[type] = new EntityRule(action.Value, maxDays);
            }
        }

        // reidentify, output, limits
        var reEnabled = false;
        var reTtl = 3600;
        var reMap = Section(root, "reidentify", ["enabled", "allowedScopes", "ttlSeconds"], errors);
        if (reMap is not null)
        {
            reEnabled = ReadBool(reMap, "enabled", false, errors);
            reTtl = ReadInt(reMap, "ttlSeconds", 60, 86400, 3600, errors);
        }

        var scan = true;
        var onLeak = PolicyAction.Redact;
        var outMap = Section(root, "output", ["scan", "onLeak"], errors);
        if (outMap is not null)
        {
            scan = ReadBool(outMap, "scan", true, errors);
            var leakNode = Scalar(outMap, "onLeak", errors);
            if (leakNode is not null)
            {
                var leakAction = ReadAction(leakNode, errors);
                if (leakAction is PolicyAction.Redact or PolicyAction.Block) onLeak = leakAction.Value;
                else if (leakAction is not null) AddError(errors, leakNode, "onLeak must be REDACT or BLOCK.");
            }
        }

        var maxChars = 32768;
        var limitsMap = Section(root, "limits", ["maxTextChars"], errors);
        if (limitsMap is not null) maxChars = ReadInt(limitsMap, "maxTextChars", 1, 100_000, 32768, errors);

        if (errors.Count > 0) return new PolicyParseResult(null, errors, warnings);

        var policy = new PolicyDocument
        {
            Name = name,
            DefaultAction = defaultAction,
            MinConfidence = minConfidence,
            Entities = entities,
            ReidentifyEnabled = reEnabled,
            ReidentifyTtlSeconds = reTtl,
            ScanOutput = scan,
            OutputOnLeak = onLeak,
            MaxTextChars = maxChars,
        };
        return new PolicyParseResult(policy, errors, warnings);
    }

    // ---- helpers ----

    private static PolicyParseResult Failed(int line, int column, string message) =>
        new(null, [new PolicyIssue(line, column, message)], []);

    private static void AddError(List<PolicyIssue> errors, YamlNode at, string message) =>
        errors.Add(new PolicyIssue((int)at.Start.Line, (int)at.Start.Column, message));

    private static YamlNode? Get(YamlMappingNode map, string key) =>
        map.Children.FirstOrDefault(kv => kv.Key is YamlScalarNode s && s.Value == key).Value;

    private static YamlScalarNode? Scalar(YamlMappingNode map, string key, List<PolicyIssue> errors, bool required = false)
    {
        var node = Get(map, key);
        if (node is null)
        {
            if (required) AddError(errors, map, $"Missing required setting '{key}'.");
            return null;
        }
        if (node is YamlScalarNode scalar) return scalar;
        AddError(errors, node, $"'{key}' must be a single value.");
        return null;
    }

    /// <summary>A named section. <paramref name="allowed"/> lists the keys it may contain; null means any key.</summary>
    private static YamlMappingNode? Section(YamlMappingNode parent, string name, string[]? allowed, List<PolicyIssue> errors)
    {
        var node = Get(parent, name);
        if (node is null) return null;
        if (node is not YamlMappingNode map) { AddError(errors, node, $"'{name}' must be a group of settings."); return null; }
        if (allowed is not null)
            foreach (var key in map.Children.Keys.OfType<YamlScalarNode>())
                if (!allowed.Contains(key.Value)) AddError(errors, key, $"Unknown setting '{name}.{key.Value}'.");
        return map;
    }

    private static PolicyAction? ReadAction(YamlScalarNode node, List<PolicyIssue> errors)
    {
        switch (node.Value?.Trim().ToUpperInvariant())
        {
            case "ALLOW": return PolicyAction.Allow;
            case "MASK": return PolicyAction.Mask;
            case "TOKENIZE": return PolicyAction.Tokenize;
            case "REDACT": return PolicyAction.Redact;
            case "SHIFT": return PolicyAction.Shift;
            case "BLOCK": return PolicyAction.Block;
        }
        AddError(errors, node, $"Unknown action '{node.Value}'. Use ALLOW, MASK, TOKENIZE, REDACT, SHIFT or BLOCK.");
        return null;
    }

    private static bool ReadBool(YamlMappingNode map, string key, bool fallback, List<PolicyIssue> errors)
    {
        var node = Scalar(map, key, errors);
        if (node is null) return fallback;
        if (bool.TryParse(node.Value, out var b)) return b;
        AddError(errors, node, $"'{key}' must be true or false.");
        return fallback;
    }

    private static int ReadInt(YamlMappingNode map, string key, int min, int max, int fallback, List<PolicyIssue> errors)
    {
        var node = Scalar(map, key, errors);
        if (node is null) return fallback;
        if (int.TryParse(node.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max) return n;
        AddError(errors, node, $"'{key}' must be a whole number from {min} to {max}.");
        return fallback;
    }
}