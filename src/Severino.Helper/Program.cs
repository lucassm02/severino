using Serilog;
using Severino.Helper;

// Used by uninstall (elevated): drop the block without going through the pipe.
if (args.Contains("--clear-hosts"))
{
    new HostsFile(HostsFile.SystemPath).Write(Array.Empty<Severino.Contracts.HostEntry>());
    return;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "Severino.Helper");

// Next to the binary: under Program Files only admins can write there, so a regular user cannot
// plant links for the SYSTEM process to write through.
builder.Services.AddSerilog(log => log
    .MinimumLevel.Information()
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "logs", "helper-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7));

builder.Services.AddSingleton<IHostsWriter>(new HostsFile(HostsFile.SystemPath));
builder.Services.AddSingleton<HelperRequestHandler>();
builder.Services.AddHostedService<PipeServer>();

builder.Build().Run();
