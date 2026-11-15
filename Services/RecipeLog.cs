using HipHipParquet.Models;

namespace HipHipParquet.Services;

/// <summary>
/// The session's edit history as recipe steps, kept in step with grid and Query Hub undo,
/// Save and Discard, so that what it lists is what the data has actually been through.
/// </summary>
public sealed class RecipeLog
{
    private readonly List<RecipeStep> _steps = [];

    public IReadOnlyList<RecipeStep> Steps => _steps;

    public event EventHandler? Changed;

    public RecipeStep Add(RecipeStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        _steps.Add(step);
        OnChanged();
        return step;
    }

    /// <summary>
    /// Grid undo restores the table to how it was before <paramref name="stepId"/>, which also
    /// reverts any later grid changes to that table, typed cell edits included.
    /// </summary>
    public void RemoveGridStepAndLater(Guid stepId)
    {
        var index = _steps.FindIndex(step => step.Id == stepId);
        if (index < 0)
            return;

        var target = _steps[index].Target;
        for (var i = _steps.Count - 1; i >= index; i--)
        {
            var step = _steps[i];
            if (step.Origin == RecipeStepOrigin.Grid &&
                string.Equals(step.Target, target, StringComparison.OrdinalIgnoreCase))
                _steps.RemoveAt(i);
        }

        OnChanged();
    }

    /// <summary>Query Hub undo reverts the most recent statement that wrote <paramref name="target"/>.</summary>
    public void RemoveLastHubStep(string target)
    {
        var index = _steps.FindLastIndex(step =>
            step.Origin == RecipeStepOrigin.QueryHub &&
            string.Equals(step.Target, target, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return;

        _steps.RemoveAt(index);
        OnChanged();
    }

    /// <summary>Drops changes that were thrown away before they reached a file.</summary>
    public void RemoveUnsaved(RecipeStepOrigin origin, string? target = null)
    {
        var removed = _steps.RemoveAll(step =>
            step.Origin == origin &&
            !step.IsSaved &&
            (target == null || string.Equals(step.Target, target, StringComparison.OrdinalIgnoreCase)));

        if (removed > 0)
            OnChanged();
    }

    public void MarkSaved(RecipeStepOrigin origin, string? target = null)
    {
        var changed = false;
        foreach (var step in _steps)
        {
            if (step.Origin != origin || step.IsSaved)
                continue;
            if (target != null && !string.Equals(step.Target, target, StringComparison.OrdinalIgnoreCase))
                continue;

            step.IsSaved = true;
            changed = true;
        }

        if (changed)
            OnChanged();
    }

    public void Clear()
    {
        if (_steps.Count == 0)
            return;

        _steps.Clear();
        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
