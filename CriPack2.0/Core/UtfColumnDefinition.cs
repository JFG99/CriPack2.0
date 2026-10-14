namespace CpkToolkit.Core;

/// <summary>One column of a @UTF table schema.</summary>
public sealed record UtfColumnDefinition(
    string Name,
    UtfColumnStorage Storage,
    UtfColumnType Type,
    object? ConstantValue);
