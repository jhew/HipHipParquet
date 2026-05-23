using HipHipParquet.Models;
using System.Text.Json;
using System.IO;

namespace HipHipParquet.Services;

/// <summary>
/// Service for managing saved views and cleaning recipes.
/// </summary>
public class WorkspaceService
{
    private readonly string _savedViewsPath;
    private readonly string _recipesPath;
    private readonly string _notebookQueriesPath;
    private readonly string _schemaTemplatesPath;
    private readonly string _markdownEditorStatePath;

    private List<SavedView> _savedViews = [];
    private List<CleaningRecipe> _savedRecipes = [];
    private List<NotebookQueryDocument> _savedNotebookQueries = [];
    private List<SchemaTemplate> _savedSchemaTemplates = [];
    private MarkdownEditorState? _markdownEditorState;
    private bool _viewsLoaded;
    private bool _recipesLoaded;
    private bool _queriesLoaded;
    private bool _schemaTemplatesLoaded;
    private bool _markdownEditorStateLoaded;

    public WorkspaceService(string? storageRoot = null)
    {
        var root = storageRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HipHipParquet");

        _savedViewsPath = Path.Combine(root, "saved-views.json");
        _recipesPath = Path.Combine(root, "recipes.json");
        _notebookQueriesPath = Path.Combine(root, "notebook-queries.json");
        _schemaTemplatesPath = Path.Combine(root, "schema-templates.json");
        _markdownEditorStatePath = Path.Combine(root, "markdown-editor-state.json");
    }

    /// <summary>
    /// Gets all saved views.
    /// </summary>
    public IReadOnlyList<SavedView> GetSavedViews()
    {
        if (!_viewsLoaded) LoadSavedViews();
        return _savedViews.AsReadOnly();
    }

    /// <summary>
    /// Saves a new view.
    /// </summary>
    public async Task SaveViewAsync(SavedView view)
    {
        if (!_viewsLoaded) LoadSavedViews();
        _savedViews.Add(view);
        await PersistViewsAsync();
    }

    /// <summary>
    /// Deletes a saved view by name.
    /// </summary>
    public async Task DeleteViewAsync(string viewName)
    {
        if (!_viewsLoaded) LoadSavedViews();
        _savedViews.RemoveAll(v => v.Name == viewName);
        await PersistViewsAsync();
    }

    /// <summary>
    /// Gets all saved recipes.
    /// </summary>
    public IReadOnlyList<CleaningRecipe> GetRecipes()
    {
        if (!_recipesLoaded) LoadRecipes();
        return _savedRecipes.AsReadOnly();
    }

    /// <summary>
    /// Saves a new recipe.
    /// </summary>
    public async Task SaveRecipeAsync(CleaningRecipe recipe)
    {
        if (!_recipesLoaded) LoadRecipes();
        _savedRecipes.Add(recipe);
        await PersistRecipesAsync();
    }

    /// <summary>
    /// Deletes a saved recipe by name.
    /// </summary>
    public async Task DeleteRecipeAsync(string recipeName)
    {
        if (!_recipesLoaded) LoadRecipes();
        _savedRecipes.RemoveAll(r => r.Name == recipeName);
        await PersistRecipesAsync();
    }

    public IReadOnlyList<NotebookQueryDocument> GetNotebookQueries()
    {
        if (!_queriesLoaded) LoadNotebookQueries();
        return _savedNotebookQueries.AsReadOnly();
    }

    public async Task SaveNotebookQueryAsync(NotebookQueryDocument query)
    {
        if (!_queriesLoaded) LoadNotebookQueries();

        var existing = _savedNotebookQueries.FirstOrDefault(item => item.Name == query.Name);
        if (existing != null)
            _savedNotebookQueries.Remove(existing);

        query.UpdatedAtUtc = DateTime.UtcNow;
        if (query.CreatedAtUtc == default)
            query.CreatedAtUtc = query.UpdatedAtUtc;

        _savedNotebookQueries.Add(query);
        await PersistNotebookQueriesAsync();
    }

    public async Task DeleteNotebookQueryAsync(string queryName)
    {
        if (!_queriesLoaded) LoadNotebookQueries();
        _savedNotebookQueries.RemoveAll(item => item.Name == queryName);
        await PersistNotebookQueriesAsync();
    }

    public IReadOnlyList<SchemaTemplate> GetSchemaTemplates()
    {
        if (!_schemaTemplatesLoaded) LoadSchemaTemplates();
        return _savedSchemaTemplates.AsReadOnly();
    }

    public async Task SaveSchemaTemplateAsync(SchemaTemplate template)
    {
        if (!_schemaTemplatesLoaded) LoadSchemaTemplates();

        var existing = _savedSchemaTemplates.FirstOrDefault(item => item.Name == template.Name);
        if (existing != null)
            _savedSchemaTemplates.Remove(existing);

        if (template.CreatedAtUtc == default)
            template.CreatedAtUtc = DateTime.UtcNow;

        _savedSchemaTemplates.Add(template);
        await PersistSchemaTemplatesAsync();
    }

    public async Task DeleteSchemaTemplateAsync(string templateName)
    {
        if (!_schemaTemplatesLoaded) LoadSchemaTemplates();
        _savedSchemaTemplates.RemoveAll(item => item.Name == templateName);
        await PersistSchemaTemplatesAsync();
    }

    public MarkdownEditorState? GetMarkdownEditorState()
    {
        if (!_markdownEditorStateLoaded) LoadMarkdownEditorState();
        return _markdownEditorState;
    }

    public async Task SaveMarkdownEditorStateAsync(MarkdownEditorState state)
    {
        _markdownEditorStateLoaded = true;
        _markdownEditorState = state;
        await PersistMarkdownEditorStateAsync();
    }

    private void LoadSavedViews()
    {
        _viewsLoaded = true;
        try
        {
            if (!File.Exists(_savedViewsPath))
                return;

            var json = File.ReadAllText(_savedViewsPath);
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
        _recipesLoaded = true;
        try
        {
            if (!File.Exists(_recipesPath))
                return;

            var json = File.ReadAllText(_recipesPath);
            _savedRecipes = JsonSerializer.Deserialize<List<CleaningRecipe>>(json) ?? [];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading recipes: {ex.Message}");
            _savedRecipes = [];
        }
    }

    private void LoadNotebookQueries()
    {
        _queriesLoaded = true;
        try
        {
            if (!File.Exists(_notebookQueriesPath))
                return;

            var json = File.ReadAllText(_notebookQueriesPath);
            _savedNotebookQueries = JsonSerializer.Deserialize<List<NotebookQueryDocument>>(json) ?? [];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading notebook queries: {ex.Message}");
            _savedNotebookQueries = [];
        }
    }

    private void LoadSchemaTemplates()
    {
        _schemaTemplatesLoaded = true;
        try
        {
            if (!File.Exists(_schemaTemplatesPath))
                return;

            var json = File.ReadAllText(_schemaTemplatesPath);
            _savedSchemaTemplates = JsonSerializer.Deserialize<List<SchemaTemplate>>(json) ?? [];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading schema templates: {ex.Message}");
            _savedSchemaTemplates = [];
        }
    }

    private void LoadMarkdownEditorState()
    {
        _markdownEditorStateLoaded = true;
        try
        {
            if (!File.Exists(_markdownEditorStatePath))
                return;

            var json = File.ReadAllText(_markdownEditorStatePath);
            _markdownEditorState = JsonSerializer.Deserialize<MarkdownEditorState>(json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading markdown editor state: {ex.Message}");
            _markdownEditorState = null;
        }
    }

    private async Task PersistViewsAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(_savedViewsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_savedViews);
            await File.WriteAllTextAsync(_savedViewsPath, json);
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
            var dir = Path.GetDirectoryName(_recipesPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_savedRecipes);
            await File.WriteAllTextAsync(_recipesPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error persisting recipes: {ex.Message}");
        }
    }

    private async Task PersistNotebookQueriesAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(_notebookQueriesPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var orderedQueries = _savedNotebookQueries
                .OrderByDescending(item => item.UpdatedAtUtc)
                .ToList();

            var json = JsonSerializer.Serialize(orderedQueries);
            await File.WriteAllTextAsync(_notebookQueriesPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error persisting notebook queries: {ex.Message}");
        }
    }

    private async Task PersistSchemaTemplatesAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(_schemaTemplatesPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var orderedTemplates = _savedSchemaTemplates
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var json = JsonSerializer.Serialize(orderedTemplates);
            await File.WriteAllTextAsync(_schemaTemplatesPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error persisting schema templates: {ex.Message}");
        }
    }

    private async Task PersistMarkdownEditorStateAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(_markdownEditorStatePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_markdownEditorState);
            await File.WriteAllTextAsync(_markdownEditorStatePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error persisting markdown editor state: {ex.Message}");
        }
    }
}
