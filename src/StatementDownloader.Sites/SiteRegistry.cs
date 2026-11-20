using Microsoft.Extensions.Logging;
using StatementDownloader.Core;
using StatementDownloader.Sites.Recipes;

namespace StatementDownloader.Sites;

public static class SiteRegistry
{
    /// <summary>
    /// Code-based sites, then the built-in recipes, then your own recipes from
    /// <paramref name="localRecipesDir"/>. A later entry with the same id replaces an earlier one,
    /// so a local copy of a recipe can fix a site before the next release.
    /// </summary>
    public static IReadOnlyList<ISiteFetcher> All(string? localRecipesDir = null, ILogger? log = null)
    {
        var sites = new Dictionary<string, ISiteFetcher>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in CodeSites()) sites[site.SiteId] = site;
        foreach (var r in RecipeLoader.BuiltIn()) sites[r.Id] = new RecipeSiteFetcher(r);
        if (localRecipesDir is not null)
            foreach (var r in RecipeLoader.FromDirectory(localRecipesDir))
            {
                if (sites.ContainsKey(r.Id)) log?.LogWarning("Site '{Site}' is overridden by your recipe {File}", r.Id, r.Source);
                sites[r.Id] = new RecipeSiteFetcher(r);
            }
        return [.. sites.Values.OrderBy(s => s.SiteId, StringComparer.Ordinal)];
    }

    /// <summary>Sites too unusual for a recipe, written against <see cref="PlaywrightSiteFetcher"/>.</summary>
    private static IEnumerable<ISiteFetcher> CodeSites() => [];
}
