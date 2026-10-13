using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Severino.App.Resources;
using Severino.App.Services;
using Severino.Core.Configuration;
using Severino.Core.Routes;

namespace Severino.App.ViewModels;

/// <param name="Summary">"4 itens · C:\projetos\meuapp\severino.json".</param>
/// <param name="Missing">The file is not where it was: "Aplicar de novo" cannot read it.</param>
public sealed record ProjectRow(string Name, string Path, string Summary, bool Missing)
{
    public bool CanReapply => !Missing;
}

/// <summary>Settings › Projetos: the project files applied here, to apply again or remove.</summary>
public sealed partial class ProjectsViewModel : ObservableObject
{
    private readonly ProjectService _projects;

    public ProjectsViewModel(ConfigService config, ProjectService projects)
    {
        _projects = projects;
        Refresh(config.Current);
        config.Changed += (_, c) => Application.Current?.Dispatcher.BeginInvoke(() => Refresh(c));
    }

    public ObservableCollection<ProjectRow> Items { get; } = [];

    [ObservableProperty]
    public partial bool HasItems { get; private set; }

    [RelayCommand]
    private Task OpenAsync() => _projects.PickAndOpenAsync();

    /// <summary>A file dropped on the window.</summary>
    public Task OpenAsync(string path) => _projects.OpenAsync(path);

    [RelayCommand]
    private Task ReapplyAsync(ProjectRow? row) => row is null ? Task.CompletedTask : _projects.OpenAsync(row.Path);

    [RelayCommand]
    private Task RemoveAsync(ProjectRow? row) => row is null ? Task.CompletedTask : _projects.RemoveAsync(row.Name);

    private void Refresh(SeverinoConfig config)
    {
        Items.Clear();
        foreach (var project in config.State.Projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var count = ProjectFiles.Count(config, project.Name);
            var items = count == 1 ? Strings.Project_ItemsOne : string.Format(Strings.Project_ItemsMany, count);
            Items.Add(new(project.Name, project.Path, string.Format(Strings.Project_RowSummary, items, project.Path), !File.Exists(project.Path)));
        }
        HasItems = Items.Count > 0;
    }
}
