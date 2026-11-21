using System.Text.RegularExpressions;

namespace StatementDownloader.Core;

/// <summary>
/// Stable identity for an account's configured actions, so a document remembers which action
/// ran on it even when the list is reordered or another action is added.
/// </summary>
public static class ActionKeys
{
    /// <summary>
    /// The key of each action, in list order: its <c>id</c> setting when given, otherwise its
    /// type, numbered when the type repeats (<c>webhook</c>, <c>webhook#2</c>). Give actions an
    /// <c>id</c> when an account has two of the same type you may reorder.
    /// </summary>
    public static IReadOnlyList<string> For(AccountConfig account)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return account.Actions.Select(a =>
        {
            if (a.TryGetValue("id", out var id) && !string.IsNullOrWhiteSpace(id)) return "id:" + id.Trim();
            var type = a.GetValueOrDefault("type") ?? "";
            var n = seen[type] = seen.GetValueOrDefault(type) + 1;
            return n == 1 ? type : $"{type}#{n}";
        }).ToList();
    }

    /// <summary>Problems with the account's action ids, for config validation.</summary>
    public static IEnumerable<string> Problems(AccountConfig account)
    {
        var ids = account.Actions.Select(a => a.GetValueOrDefault("id")).Where(id => id is not null).ToList();
        foreach (var id in ids.Where(id => !Regex.IsMatch(id!, "^[A-Za-z0-9][A-Za-z0-9_-]*$")))
            yield return $"action id '{id}' may contain only letters, digits, '-' and '_'.";
        foreach (var dup in ids.GroupBy(id => id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"action id '{dup.Key}' is used twice.";
    }
}
