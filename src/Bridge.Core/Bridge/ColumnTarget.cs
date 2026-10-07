namespace Bridge.Core.Bridge;

public sealed record ColumnTarget
{
    public int? Index { get; init; }
    public string? Name { get; init; }
    public string? Board { get; init; }

    public object ToOscArgument()
    {
        if (!string.IsNullOrWhiteSpace(Name))
        {
            return Name;
        }

        if (Index is > 0)
        {
            return Index.Value;
        }

        throw new InvalidOperationException("Column target requires Name or a positive Index.");
    }

    public bool Matches(int? index, string? name)
    {
        if (!string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(name))
        {
            return string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);
        }

        return Index.HasValue && index == Index;
    }
}
