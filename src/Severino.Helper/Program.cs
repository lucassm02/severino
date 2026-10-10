using Serilog;
using Severino.Contracts;
using Severino.Helper;

// Used by uninstall (elevated): drop both blocks and the approvals without going through the pipe.
// Lines outside Severino's blocks, edited or not, stay as they are.
if (args.Contains("--clear-hosts"))
{
    var hosts = new HostsFile(HostsFile.SystemPath);
    hosts.Write(Array.Empty<HostEntry>());
    hosts.WriteDns(Array.Empty<HostEntry>());
    new DnsApprovals().Clear();
    return 0;
}

// Run elevated by the app, behind Windows' UAC prompt: approves public addresses for DNS entries.
if (args is ["--approve-dns", var encoded])
    return ApproveDns.Run(encoded);

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
builder.Services.AddSingleton<IDnsApprovals>(new DnsApprovals());
builder.Services.AddSingleton(sp => new HelperRequestHandler(
    sp.GetRequiredService<IHostsWriter>(), sp.GetRequiredService<IDnsApprovals>(), sp.GetRequiredService<ILogger<HelperRequestHandler>>()));
builder.Services.AddHostedService<PipeServer>();

builder.Build().Run();
return 0;
