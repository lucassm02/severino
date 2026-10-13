using System.IO;
using Microsoft.Extensions.Logging;
using Severino.App.Resources;
using Severino.Core.Configuration;
using Severino.Core.Dns;
using Severino.Core.Routes;

namespace Severino.App.Services;

/// <summary>
/// Project files from the window: opening one (the dialog, a file dropped on the window, or
/// "Aplicar de novo"), always through a preview; removing a project; and saving a route group as one.
/// </summary>
public sealed class ProjectService(ConfigService config, ExternalHosts external, ILogger<ProjectService> logger)
{
    public static string OpenFilter => Strings.Project_Filter;

    /// <summary>Picks a file and opens it.</summary>
    public Task<bool> PickAndOpenAsync() =>
        DialogService.PickOpenPath(OpenFilter) is { } path ? OpenAsync(path) : Task.FromResult(false);

    /// <summary>Reads, previews and, when confirmed, applies the project file at <paramref name="path"/>.</summary>
    /// <returns>True when it was applied.</returns>
    public async Task<bool> OpenAsync(string path)
    {
        RouteFile file;
        try
        {
            file = ProjectFiles.Read(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidRouteFileException)
        {
            await DialogService.ShowInfoAsync(Strings.Project_OpenTitle, string.Format(Strings.Project_OpenFailed, path, ex.Message));
            return false;
        }

        var names = external.Names;
        var plan = ProjectFiles.Plan(file, config.Current, names);
        if (!await DialogService.ConfirmProjectAsync(plan, path))
            return false;

        // Planned again on the config of this moment, so nothing changed meanwhile is lost.
        config.Update(c => ProjectFiles.Remember(ProjectFiles.Plan(file, c, names).Next, file.Name, Path.GetFullPath(path)));
        logger.LogInformation("Project {Project} applied from {Path}", file.Name, path);
        return true;
    }

    public async Task RemoveAsync(string name)
    {
        var count = ProjectFiles.Count(config.Current, name);
        if (!await DialogService.ConfirmAsync(Strings.Project_RemoveTitle, string.Format(Strings.Project_RemoveText, name, count), Strings.Common_Remove))
            return;
        config.Update(c => ProjectFiles.Remove(c, name));
        logger.LogInformation("Project {Project} removed", name);
    }

    /// <summary>The routes of <paramref name="group"/>, and what they go to, into a severino.json.</summary>
    public async Task SaveGroupAsync(string group)
    {
        var routes = config.Current.Routes.Where(r => r.Group == group).ToList();
        if (routes.Count == 0 || DialogService.PickSavePath(ProjectFiles.FileName, OpenFilter) is not { } path)
            return;
        try
        {
            await File.WriteAllTextAsync(path, ProjectFiles.Export(group, routes, config.Current) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await DialogService.ShowInfoAsync(Strings.Project_SaveTitle, string.Format(Strings.Common_CouldNotWriteFile, ex.Message));
        }
    }
}
