using System.Text;
using System.Text.Json;
using UnVault.Core.Util;

namespace UnVault.Core.Fab;

/// <summary>
/// What a listing's page on Fab shows under "Technical details", from GET fab.com/i/listings/{id}/asset-formats/unreal-engine:
/// the seller's own notes, then whatever of Fab's fields for that kind of product the seller filled in. Public, like the
/// page. Which fields a kind has isn't told anywhere, so they're read as they come and named by Fab's own names for them.
/// </summary>
public static class FabTechnicalDetails
{
    /// <summary>The details as plain text: the notes, a blank line, then a line per field. Empty when there are none.</summary>
    public static string ToText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("techDetails", out var details) || details.ValueKind != JsonValueKind.Object)
            return "";

        var parts = new List<string>();
        // One of them is filled in: the kind the product is (threeDModel, toolAndPlugin, vfx …).
        foreach (var kind in details.EnumerateObject().Where(k => k.Value.ValueKind == JsonValueKind.Object))
        {
            var facts = new List<string>();
            foreach (var field in kind.Value.EnumerateObject())
            {
                if (field.Name == "additionalInformation")
                {
                    if (field.Value.ValueKind == JsonValueKind.String && HtmlText.ToPlain(field.Value.GetString()!) is { Length: > 0 } notes)
                        parts.Add(notes);
                }
                else if (field.Name != "distributionMethod" && Value(field.Value) is { } value) // that one the Library tells as the item's type
                {
                    facts.Add($"{Label(field.Name)}: {value}");
                }
            }
            if (facts.Count > 0)
                parts.Add(string.Join('\n', facts));
        }
        return string.Join("\n\n", parts);
    }

    /// <summary>A field's value in words; null for one that's empty, so its line is left out.</summary>
    private static string? Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.String when value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) => text.Trim().Replace('_', ' '),
        JsonValueKind.Array when value.EnumerateArray().Select(Value).OfType<string>().ToList() is { Count: > 0 } items => string.Join(", ", items),
        // A range, as Fab gives counts: { "lower": 1000, "upper": 5000 }.
        JsonValueKind.Object when value.TryGetProperty("lower", out var lower) && value.TryGetProperty("upper", out var upper)
            && Value(lower) is { } from && Value(upper) is { } to => from == to ? from : $"{from} – {to}",
        _ => null,
    };

    /// <summary>Fab's name for a field, as words: "uniqueMeshesCount" → "Unique meshes count".</summary>
    internal static string Label(string name)
    {
        var label = new StringBuilder(name.Length + 4);
        foreach (char c in name)
        {
            if (char.IsUpper(c) && label.Length > 0)
                label.Append(' ');
            label.Append(label.Length == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
        }
        return label.ToString();
    }
}
