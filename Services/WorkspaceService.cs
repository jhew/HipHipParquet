using HipHipParquet.Models;
using System.Text.Json;
using System.IO;

namespace HipHipParquet.Services;

/// <summary>
/// Service for managing saved views and cleaning recipes.
/// </summary>
public class WorkspaceService
{
    private static readonly string SavedViewsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HipHipParquet",
        "saved-views.json");

    private static readonly string RecipesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HipHipParquet",
        "recipes.json");

    private List<SavedView> _savedViews = [];
    private List<CleaningRecipe> _savedRecipes = [];

    public WorkspaceService()
    {
        LoadSavedViews();
        LoadRecipes();
    }

    /// <summary>
    /// Gets all saved views.
    /// </summary>
    public IReadOnlyList<SavedView> GetSavedViews() => _savedViews.AsReadOnly();

    /// <summary>
    /// Saves a new view.
    /// </summary>
    public async Task SaveViewAsync(SavedView view)
    {
        _savedViews.Add(view);
        await PersistViewsAsync();
    }

    /// <summary>
    /// Deletes a saved view by name.
    /// </summary>
    public async Task DeleteViewAsync(string viewName)
    {
        _savedViews.RemoveAll(v => v.Name == viewName);
        await PersistViewsAsync();
    }

    /// <summary>
    /// Gets all saved recipes.
    /// </summary>
    public IReadOnlyList<CleaningRecipe> GetRecipes() => _savedRecipes.AsReadOnly();

    /// <summary>
    /// Saves a new recipe.
    /// </summary>
    public async Task SaveRecipeAsync(CleaningRecipe recipe)
    {
        _savedRecipes.Add(recipe);
        await PersistRecipesAsync();
    }

    /// <summary>
    /// Deletes a saved recipe by name.
    /// </summary>
    public async Task DeleteRecipeAsync(string recipeName)
    {
        _savedRecipes.RemoveAll(r => r.Name == recipeName);
        await PersistRecipesAsync();
    }

    private void LoadSavedViews()
    {
        try
        {
            if (!File.Exists(SavedViewsPath))
                return;

            var json = File.ReadAllText(SavedViewsPath);
            _savedViews = JsonSerializer.Deserialize<List<SavedView>>(json) ?? [];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading saved views: {ex.Message}");
            _savedViews = [];
        }
    }

    private void LoadRecipes()
    {
        try
        {
            if (!File.Exists(RecipesPath))
                return;

            var json = File.ReadAllText(RecipesPath);
            _savedRecipes = JsonSerializer.Deserialize<List<CleaningRecipe>>(json) ?? [];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading recipes: {ex.Message}");
            _savedRecipes = [];
        }
    }

    private async Task PersistViewsAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(SavedViewsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_savedViews);
            await File.WriteAllTextAsync(SavedViewsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error persisting saved views: {ex.Message}");
        }
    }

    private async Task PersistRecipesAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(RecipesPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_savedRecipes);
            await File.WriteAllTextAsync(RecipesPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error persisting recipes: {ex.Message}");
        }
    }
}
